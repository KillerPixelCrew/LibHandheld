using System.Numerics;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Windows;

/// <summary>Owns a handheld IMU exposed through the legacy Sensor API.</summary>
/// <remarks>
///     <para>
///         Shared by every handheld package. The standard gyrometer and accelerometer are what WinRT's
///         defaults wrap; the "Physical" pair is Intel's sensor stack on the MSI Claw.
///     </para>
///     <para>
///         On the Claw A2VM and CG3EM, Intel's sensor stack classifies both LSM6DSO physical sensors as
///         <c>SENSOR_TYPE_CUSTOM</c>. WinRT therefore does not expose the accelerometer and suppresses
///         unchanged gyroscope readings. This COM edge reads the two physical collections directly and
///         uses their hardware report counter to distinguish a new sample from a repeated <c>GetData</c>
///         result.
///     </para>
///     <para>
///         The A1M and A8 declare no such sensors in HC, which reads them through WinRT's default
///         Gyrometer and Accelerometer. Those wrap the standard <c>SENSOR_TYPE_GYROMETER_3D</c> and
///         <c>SENSOR_TYPE_ACCELEROMETER_3D</c> sensors this class binds for them, with the standard
///         motion fields and the report timestamp in place of a counter. Going through the same COM edge
///         keeps the per-report cost at one wrapper released on the spot, where WinRT's projection leaves
///         finalizable objects for every event.
///     </para>
/// </remarks>
internal sealed partial class LegacyMotionSensors : IDisposable
{
    private const string ExpectedAccelerometerName = "Physical Accelerometer";
    private const string ExpectedGyrometerName = "Physical Gyrometer";

    private static readonly Guid CustomSensorType =
        new("E83AF229-8640-4D18-A213-E22675EBB2C3");

    private static readonly Guid CustomDataFormat =
        new("B14C764F-07CF-41E8-9D82-EBE3D0776A6F");

    private static readonly Guid CommonProperties =
        new("7F8383EC-D3EC-495C-A8CF-B8BBE85C2920");

    private static readonly PropertyKey DevicePathProperty = new(CommonProperties, 15);
    private static readonly PropertyKey MinimumReportInterval = new(CommonProperties, 12);
    private static readonly PropertyKey CurrentReportInterval = new(CommonProperties, 13);
    private static readonly PropertyKey AxisX = new(CustomDataFormat, 7);
    private static readonly PropertyKey AxisY = new(CustomDataFormat, 8);
    private static readonly PropertyKey AxisZ = new(CustomDataFormat, 9);
    private static readonly PropertyKey HardwareReportCounter = new(CustomDataFormat, 34);

    private static readonly Guid StandardGyrometerType = new("09485F5A-759E-42C2-BD4B-A349B75C8643");
    private static readonly Guid StandardAccelerometerType = new("C2FB0F5F-E2D2-4C78-BCD0-352A9582819D");

    /// <summary><c>SENSOR_DATA_TYPE_MOTION_GUID</c>: acceleration in g at 2-4, angular velocity in dps at 10-12.</summary>
    private static readonly Guid StandardMotionFormat = new("3F8A69A2-07C5-4E48-A965-CD797AAB56D5");

    private static readonly PropertyKey[] PhysicalAxes = [AxisX, AxisY, AxisZ];

    private static readonly PropertyKey[] StandardGyroAxes =
    [
        new(StandardMotionFormat, 10), new(StandardMotionFormat, 11), new(StandardMotionFormat, 12)
    ];

    private static readonly PropertyKey[] StandardAccelerometerAxes =
    [
        new(StandardMotionFormat, 2), new(StandardMotionFormat, 3), new(StandardMotionFormat, 4)
    ];

    private readonly Lock _gate = new();
    private readonly SensorProfile _profile;
    private ISensor? _accelerometer;
    private IntervalState _accelerometerInterval;
    private ISensor? _gyrometer;
    private IntervalState _gyrometerInterval;

    /// <summary>The hardware counter, or the timestamp's ticks where the sensor has no counter.</summary>
    private ulong? _lastReportKey;

    private LegacyMotionSensors(
        SensorProfile profile,
        ISensor? accelerometer,
        ISensor gyrometer,
        string accelerometerPath,
        string gyrometerPath)
    {
        _profile = profile;
        _accelerometer = accelerometer;
        _gyrometer = gyrometer;
        AccelerometerPath = accelerometerPath;
        GyrometerPath = gyrometerPath;
    }

    /// <summary>The Sensor API path for the Intel ISS physical accelerometer.</summary>
    private string AccelerometerPath { get; }

    /// <summary>The Sensor API path for the Intel ISS physical gyrometer.</summary>
    private string GyrometerPath { get; }

    /// <summary>Unsubscribes callbacks, attempts to restore report intervals, and releases owned COM sensors.</summary>
    /// <remarks>
    ///     Idempotent after successful cleanup. Unsubscription may wait for native callbacks; do not call from
    ///     a reading callback. Restoration failures are traced and do not claim verified hardware recovery.
    /// </remarks>
    public void Dispose()
    {
        // Before the handles go: a sink left registered on a released sensor is a callback into
        // a freed object.
        Unsubscribe();
        lock (_gate)
        {
            RestoreInterval(_gyrometer, _profile.GyrometerName, _gyrometerInterval);
            RestoreInterval(_accelerometer, _profile.AccelerometerName, _accelerometerInterval);
            Release(_gyrometer);
            Release(_accelerometer);
            _gyrometer = null;
            _accelerometer = null;
        }
    }

    /// <summary>
    ///     Finds and configures a gyrometer and an accelerometer, each independently, trying the sources
    ///     in the order given; HC takes the standard sensors WinRT's defaults wrap first. A missing
    ///     accelerometer leaves a gyro-only source, as it does in HC.
    /// </summary>
    /// <param name="sources">Where to look, in order of preference.</param>
    /// <returns>
    ///     A sensor set the caller must dispose or transfer to <see cref="LegacyMotionStream" />;
    ///     null when no usable gyrometer is found or supported Sensor API acquisition fails.
    /// </returns>
    /// <remarks>
    ///     Requests the fastest supported reporting intervals and captures originals for disposal.
    ///     This opens native resources and can change report timing; it is not passive detection.
    ///     Gyrometer and accelerometer may be selected from different source families.
    /// </remarks>
    public static LegacyMotionSensors? TryOpen(IReadOnlyList<LegacyMotionSensorSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        object? managerObject = null;
        ISensor? accelerometer = null;
        ISensor? gyrometer = null;
        try
        {
            managerObject = new SensorManagerClass();
            var manager = (ISensorManager)managerObject;
            LegacyMotionSensorSource? gyrometerSource = null;
            LegacyMotionSensorSource? accelerometerSource = null;
            string? gyrometerPath = null;
            string? accelerometerPath = null;
            foreach (var source in sources)
            {
                if (gyrometer is null)
                {
                    gyrometer = Find(manager, source, true, out gyrometerPath);
                    gyrometerSource = gyrometer is null ? null : source;
                }

                if (accelerometer is null)
                {
                    accelerometer = Find(manager, source, false, out accelerometerPath);
                    accelerometerSource = accelerometer is null ? null : source;
                }
            }

            if (gyrometer is null)
            {
                DeviceTrace.Info(
                    "motion",
                    $"Legacy Sensor API found no gyrometer ({string.Join(", ", sources)}); accelerometer="
                    + $"{accelerometer is not null}.");
                return null;
            }

            var physicalGyro = gyrometerSource is LegacyMotionSensorSource.Physical;
            SensorProfile profile = new(
                physicalGyro ? ExpectedGyrometerName : "Gyrometer",
                accelerometerSource is LegacyMotionSensorSource.Physical ? ExpectedAccelerometerName : "Accelerometer",
                physicalGyro ? PhysicalAxes : StandardGyroAxes,
                accelerometerSource is LegacyMotionSensorSource.Physical ? PhysicalAxes : StandardAccelerometerAxes,
                physicalGyro && Supports(gyrometer, HardwareReportCounter));
            LegacyMotionSensors candidate = new(
                profile,
                accelerometer,
                gyrometer,
                accelerometerPath ?? "<none>",
                gyrometerPath ?? "<no path>");
            accelerometer = null;
            gyrometer = null;
            return Activate(candidate,
                $"gyro {gyrometerSource}, accelerometer {accelerometerSource?.ToString() ?? "none"}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            DeviceTrace.Failure("motion", "Legacy Sensor API discovery failed", ex);
            return null;
        }
        finally
        {
            Release(accelerometer);
            Release(gyrometer);
            Release(managerObject);
        }
    }

    private static ISensor? Find(ISensorManager manager, LegacyMotionSensorSource source, bool gyrometer,
        out string? path)
    {
        return source switch
        {
            LegacyMotionSensorSource.Physical => FindPhysical(
                manager,
                gyrometer ? ExpectedGyrometerName : ExpectedAccelerometerName,
                out path),
            _ => FindStandard(
                manager,
                gyrometer ? StandardGyrometerType : StandardAccelerometerType,
                gyrometer ? StandardGyroAxes : StandardAccelerometerAxes,
                out path)
        };
    }

    /// <summary>
    ///     HC's <c>WindowsSensorManager</c> match for a declared sensor: the friendly name and the three
    ///     value fields, nothing else.
    /// </summary>
    private static ISensor? FindPhysical(ISensorManager manager, string friendlyName, out string? path)
    {
        return FirstMatching(manager, CustomSensorType, out path, sensor =>
            sensor.GetFriendlyName(out var name) >= 0
            && MatchesExpectedIdentity(name, friendlyName)
            && Supports(sensor, AxisX)
            && Supports(sensor, AxisY)
            && Supports(sensor, AxisZ));
    }

    /// <summary>The first standard sensor of a type with the motion fields, as WinRT's GetDefault would pick.</summary>
    private static ISensor? FindStandard(ISensorManager manager, Guid type, PropertyKey[] axes, out string? path)
    {
        return FirstMatching(manager, type, out path, sensor =>
            Supports(sensor, axes[0]) && Supports(sensor, axes[1]) && Supports(sensor, axes[2]));
    }

    private static ISensor? FirstMatching(ISensorManager manager, Guid type, out string? path,
        Func<ISensor, bool> matches)
    {
        path = null;
        ISensorCollection? collection = null;
        try
        {
            var requested = type;
            if (manager.GetSensorsByType(ref requested, out collection) < 0
                || collection is null
                || collection.GetCount(out var count) < 0)
            {
                return null;
            }

            for (uint index = 0; index < count; index++)
            {
                ISensor? sensor = null;
                try
                {
                    if (collection.GetAt(index, out sensor) < 0 || sensor is null || !matches(sensor))
                    {
                        continue;
                    }

                    path = ReadStringProperty(sensor, DevicePathProperty);
                    var found = sensor;
                    sensor = null;
                    return found;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                    DeviceTrace.Failure("motion", $"Legacy sensor {index} could not be inspected", ex);
                }
                finally
                {
                    Release(sensor);
                }
            }

            return null;
        }
        finally
        {
            Release(collection);
        }
    }

    private static LegacyMotionSensors? Activate(LegacyMotionSensors candidate, string kind)
    {
        if (candidate.TryRead(out _, out var error) == MotionSensorReadResult.Failed)
        {
            DeviceTrace.Warn("motion", $"The IMU ({kind}) was present but its fields could not be read: {error}");
            candidate.Dispose();
            return null;
        }

        candidate.ConfigureFastestIntervals();
        DeviceTrace.Info(
            "motion",
            $"Legacy IMU active ({kind}): gyro={candidate.GyrometerPath}; accel={candidate.AccelerometerPath}.");
        return candidate;
    }

    /// <summary>Reads one gyroscope report and, when available, the current accelerometer report.</summary>
    /// <param name="reading">The sensor-space values and timestamp; meaningful only when fresh.</param>
    /// <param name="error">The decisive COM or value failure when the read failed.</param>
    /// <returns>
    ///     Fresh with a usable reading, Duplicate for a repeated report key, or Failed with an error.
    ///     The key is the hardware counter when supported, otherwise the sensor timestamp.
    ///     A present accelerometer that fails to read makes the whole poll fail.
    /// </returns>
    public MotionSensorReadResult TryRead(out MotionSensorReading reading, out MotionSensorReadError? error)
    {
        lock (_gate)
        {
            reading = default;
            error = null;
            if (_gyrometer is not { } gyrometer)
            {
                error = new MotionSensorReadError(MotionSensorErrorKind.Closed);
                return MotionSensorReadResult.Failed;
            }

            var result = TryReadGyrometer(
                gyrometer,
                out var angularVelocity,
                out var timestamp,
                out var reportKey,
                out error);
            if (result != MotionSensorReadResult.Fresh)
            {
                return result;
            }

            // Polling runs faster than the gyrometer report interval. Qualify that report before
            // paying for the accelerometer's synchronous GetData, which can also wait for a changed
            // report while the device is still.
            Vector3? acceleration = null;
            if (_accelerometer is { } accelerometer)
            {
                if (!TryReadVector(accelerometer, _profile.AccelerometerAxes, out var read, out error))
                {
                    return MotionSensorReadResult.Failed;
                }

                acceleration = read;
            }

            _lastReportKey = reportKey;
            reading = new MotionSensorReading(angularVelocity, acceleration, timestamp);
            return MotionSensorReadResult.Fresh;
        }
    }

    /// <summary>HC's legacy match: the friendly name the device JSON declares, case aside.</summary>
    /// <param name="friendlyName">Friendly name reported by the sensor, or null when absent.</param>
    /// <param name="expectedFriendlyName">Exact name declared by the device motion profile.</param>
    /// <returns>Whether the names match using ordinal case-insensitive comparison without trimming.</returns>
    internal static bool MatchesExpectedIdentity(string? friendlyName, string expectedFriendlyName)
    {
        return string.Equals(friendlyName, expectedFriendlyName, StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureFastestIntervals()
    {
        lock (_gate)
        {
            _gyrometerInterval = ConfigureFastestInterval(_gyrometer, _profile.GyrometerName);
            _accelerometerInterval = ConfigureFastestInterval(
                _accelerometer,
                _profile.AccelerometerName,
                _gyrometerInterval.Applied);
        }
    }

    /// <param name="sensor">The sensor to configure.</param>
    /// <param name="name">The sensor's name for the log.</param>
    /// <param name="floor">
    ///     The slowest interval worth asking for, in ms. The accelerometer is only ever read once per
    ///     gyrometer report, so its driver minimum of 2 ms bought nothing and, with event delivery,
    ///     cost five hundred callbacks a second in the driver host and the notification thread.
    ///     Read off the virtual Deck with a raw HID handle, the accelerometer value changes about 80
    ///     times a second at either request, in alternating 8 and 16 ms steps, so the floor changes
    ///     nothing the consumer receives (docs/perf, 2026-09-26).
    /// </param>
    private static IntervalState ConfigureFastestInterval(ISensor? sensor, string name, uint floor = 0)
    {
        if (sensor is null
            || !TryReadUnsignedProperty(sensor, CurrentReportInterval, out var original)
            || !TryReadUnsignedProperty(sensor, MinimumReportInterval, out var minimum))
        {
            DeviceTrace.Warn("motion", $"{name} report interval properties could not be read.");
            return default;
        }

        var requested = Math.Max(minimum == 0 ? 10u : minimum, floor);
        if (original == requested)
        {
            DeviceTrace.Info("motion", $"{name} report interval is {requested} ms.");
            return new IntervalState(original, requested, false);
        }

        if (!TrySetUnsignedProperty(sensor, CurrentReportInterval, requested, out var error))
        {
            DeviceTrace.Warn(
                "motion",
                $"{name} could not request its fastest {requested} ms report interval: {error}.");
            return new IntervalState(original, requested, false);
        }

        if (!TryReadUnsignedProperty(sensor, CurrentReportInterval, out var effective)
            || effective != requested)
        {
            DeviceTrace.Warn(
                "motion",
                $"{name} did not confirm its requested {requested} ms report interval.");
            // SetProperties succeeded, so retain ownership state even when readback did not. On
            // release, restoration occurs only if the current value still equals our request.
            return new IntervalState(original, requested, true);
        }

        DeviceTrace.Info(
            "motion",
            $"{name} report interval changed from {original} ms to the driver minimum {effective} ms.");
        return new IntervalState(original, effective, true);
    }

    private static void RestoreInterval(ISensor? sensor, string name, IntervalState state)
    {
        if (sensor is null || !state.Changed)
        {
            return;
        }

        if (!TryReadUnsignedProperty(sensor, CurrentReportInterval, out var current))
        {
            DeviceTrace.Warn("motion", $"{name} report interval could not be read during release.");
            return;
        }

        // Do not overwrite a newer choice made by another client while this cycle was active.
        if (current != state.Applied)
        {
            DeviceTrace.Info(
                "motion",
                $"{name} report interval is now {current} ms; leaving that external value unchanged.");
            return;
        }

        if (!TrySetUnsignedProperty(sensor, CurrentReportInterval, state.Original, out var error))
        {
            DeviceTrace.Warn(
                "motion",
                $"{name} report interval could not be restored to {state.Original} ms: {error}.");
        }
    }

    private static bool Supports(ISensor sensor, PropertyKey key)
    {
        return sensor.SupportsDataField(ref key, out var supported) >= 0 && supported != 0;
    }

    private MotionSensorReadResult TryReadGyrometer(
        ISensor sensor,
        out Vector3 angularVelocity,
        out DateTimeOffset timestamp,
        out ulong reportKey,
        out MotionSensorReadError? error)
    {
        angularVelocity = default;
        timestamp = default;
        reportKey = 0;
        error = null;
        nint reportPointer = 0;
        try
        {
            var result = sensor.GetData(out reportPointer);
            var report = new SensorReport(reportPointer);
            if (result < 0 || reportPointer == 0)
            {
                error = new MotionSensorReadError(MotionSensorErrorKind.GyrometerData, result);
                return MotionSensorReadResult.Failed;
            }

            // Poll faster than the 10 ms report interval and roughly four polls in five repeat the
            // previous report. Qualify the report before marshalling anything else out of it: the
            // three axis PROPVARIANTs and the SYSTEMTIME below are the bulk of this path's cost and
            // would only be discarded.
            if (!TryReadReportKey(report, out reportKey, out timestamp, out error))
            {
                return MotionSensorReadResult.Failed;
            }

            if (_lastReportKey == reportKey)
            {
                return MotionSensorReadResult.Duplicate;
            }

            if (!TryReadVector(report, _profile.GyroAxes, out angularVelocity, out error))
            {
                return MotionSensorReadResult.Failed;
            }

            if (_profile.HasCounter && !TryReadTimestamp(report, out timestamp, out error))
            {
                return MotionSensorReadResult.Failed;
            }

            return MotionSensorReadResult.Fresh;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            error = new MotionSensorReadError(MotionSensorErrorKind.NativeFailure, ex.HResult);
            return MotionSensorReadResult.Failed;
        }
        finally
        {
            if (reportPointer != 0)
            {
                Marshal.Release(reportPointer);
            }
        }
    }

    /// <summary>
    ///     What tells one gyrometer report from the last: the hardware counter on the physical sensor,
    ///     otherwise the report timestamp, which is then already read.
    /// </summary>
    private bool TryReadReportKey(
        SensorReport report,
        out ulong key,
        out DateTimeOffset timestamp,
        out MotionSensorReadError? error)
    {
        timestamp = default;
        if (_profile.HasCounter)
        {
            var read = TryReadUnsignedValue(report, HardwareReportCounter, out var counter, out error);
            key = counter;
            return read;
        }

        key = 0;
        if (!TryReadTimestamp(report, out timestamp, out error))
        {
            return false;
        }

        key = (ulong)timestamp.UtcTicks;
        return true;
    }

    private bool TryReadTimestamp(SensorReport report, out DateTimeOffset timestamp, out MotionSensorReadError? error)
    {
        error = null;
        var result = report.GetTimestamp(out var systemTime);
        if (result >= 0 && systemTime.TryToUtc(out timestamp))
        {
            return true;
        }

        timestamp = default;
        error = new MotionSensorReadError(MotionSensorErrorKind.Timestamp, result);
        return false;
    }

    private static bool TryReadVector(ISensor sensor, PropertyKey[] axes, out Vector3 value,
        out MotionSensorReadError? error)
    {
        value = default;
        error = null;
        nint reportPointer = 0;
        try
        {
            var result = sensor.GetData(out reportPointer);
            var report = new SensorReport(reportPointer);
            if (result >= 0 && reportPointer != 0)
            {
                return TryReadVector(report, axes, out value, out error);
            }

            error = new MotionSensorReadError(MotionSensorErrorKind.AccelerometerData, result);
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            error = new MotionSensorReadError(MotionSensorErrorKind.NativeFailure, ex.HResult);
            return false;
        }
        finally
        {
            if (reportPointer != 0)
            {
                Marshal.Release(reportPointer);
            }
        }
    }

    private static bool TryReadVector(
        SensorReport report,
        PropertyKey[] axes,
        out Vector3 value,
        out MotionSensorReadError? error)
    {
        value = default;
        if (!TryReadNumericValue(report, axes[0], out var x, out error)
            || !TryReadNumericValue(report, axes[1], out var y, out error)
            || !TryReadNumericValue(report, axes[2], out var z, out error))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static string? ReadStringProperty(ISensor sensor, PropertyKey key)
    {
        var result = sensor.GetProperty(ref key, out var value);
        if (result < 0)
        {
            return null;
        }

        try
        {
            return value.VariantType == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    private static bool TryReadUnsignedProperty(ISensor sensor, PropertyKey key, out uint result)
    {
        result = 0;
        var hresult = sensor.GetProperty(ref key, out var value);
        if (hresult < 0)
        {
            return false;
        }

        try
        {
            if (value.VariantType != 19)
            {
                return false;
            }

            result = value.UInt32;
            return true;
        }
        finally
        {
            _ = PropVariantClear(ref value);
        }
    }

    private static bool TrySetUnsignedProperty(
        ISensor sensor,
        PropertyKey key,
        uint value,
        out string? error)
    {
        error = null;
        IPortableDeviceValues? properties = null;
        IPortableDeviceValues? results = null;
        try
        {
            properties = (IPortableDeviceValues)new PortableDeviceValuesClass();
            var result = properties.SetUnsignedIntegerValue(ref key, value);
            if (result < 0)
            {
                error = $"building the property set returned 0x{result:X8}";
                return false;
            }

            result = sensor.SetProperties(properties, out results);
            if (result >= 0)
            {
                return true;
            }

            error = $"SetProperties returned 0x{result:X8}";
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            Release(results);
            Release(properties);
        }
    }

    private static bool TryReadNumericValue(
        SensorReport report,
        PropertyKey key,
        out float value,
        out MotionSensorReadError? error)
    {
        value = 0;
        error = null;
        var result = report.GetSensorValue(ref key, out var variant);
        if (result < 0)
        {
            error = new MotionSensorReadError(MotionSensorErrorKind.Field, result, key.PropertyId);
            return false;
        }

        try
        {
            double? numeric = variant.VariantType switch
            {
                3 => variant.Int32,
                19 => variant.UInt32,
                4 => variant.Single,
                5 => variant.Double,
                _ => null
            };
            if (numeric is not { } present || !double.IsFinite(present))
            {
                error = new MotionSensorReadError(MotionSensorErrorKind.VariantType, PropertyId: key.PropertyId,
                    VariantType: variant.VariantType);
                return false;
            }

            value = (float)present;
            if (float.IsFinite(value))
            {
                return true;
            }

            error = new MotionSensorReadError(MotionSensorErrorKind.NonFiniteValue, PropertyId: key.PropertyId);
            return false;
        }
        finally
        {
            _ = PropVariantClear(ref variant);
        }
    }

    private static bool TryReadUnsignedValue(
        SensorReport report,
        PropertyKey key,
        out uint value,
        out MotionSensorReadError? error)
    {
        value = 0;
        error = null;
        var result = report.GetSensorValue(ref key, out var variant);
        if (result < 0)
        {
            error = new MotionSensorReadError(MotionSensorErrorKind.Field, result, key.PropertyId);
            return false;
        }

        try
        {
            if (variant.VariantType != 19)
            {
                error = new MotionSensorReadError(MotionSensorErrorKind.VariantType, PropertyId: key.PropertyId,
                    VariantType: variant.VariantType);
                return false;
            }

            value = variant.UInt32;
            return true;
        }
        finally
        {
            _ = PropVariantClear(ref variant);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PropVariant value);

    private readonly record struct IntervalState(uint Original, uint Applied, bool Changed);

    /// <summary>The chosen sensors: names for the log, the axis fields and whether the gyro has a counter.</summary>
    private sealed record SensorProfile(
        string GyrometerName,
        string AccelerometerName,
        PropertyKey[] GyroAxes,
        PropertyKey[] AccelerometerAxes,
        bool HasCounter);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public int Int32;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public float Single;
        [FieldOffset(8)] public double Double;
        [FieldOffset(8)] public nint Pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;

        public readonly bool TryToUtc(out DateTimeOffset timestamp)
        {
            timestamp = default;
            if (Year is < 1 or > 9999 || Month is < 1 or > 12 || Day < 1
                || Day > DateTime.DaysInMonth(Year, Month) || Hour > 23 || Minute > 59 || Second > 59 ||
                Milliseconds > 999)
            {
                return false;
            }

            timestamp = new DateTimeOffset(Year, Month, Day, Hour, Minute, Second, Milliseconds, TimeSpan.Zero);
            return true;
        }
    }

    [ComImport]
    [Guid("BD77DB67-45A8-42DC-8D00-6DCF15F8377A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorManager
    {
        [PreserveSig]
        int GetSensorsByCategory([In] ref Guid category, out ISensorCollection? sensors);

        [PreserveSig]
        int GetSensorsByType([In] ref Guid type, out ISensorCollection? sensors);

        [PreserveSig]
        int GetSensorByID([In] ref Guid id, out ISensor? sensor);

        [PreserveSig]
        int SetEventSink(nint events);

        [PreserveSig]
        int RequestPermissions(nint window, ISensorCollection sensors, [MarshalAs(UnmanagedType.Bool)] bool modal);
    }

    [ComImport]
    [Guid("23571E11-E545-4DD8-A337-B89BF44B10DF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorCollection
    {
        [PreserveSig]
        int GetAt(uint index, out ISensor? sensor);

        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Add(ISensor sensor);

        [PreserveSig]
        int Remove(ISensor sensor);

        [PreserveSig]
        int RemoveByID([In] ref Guid id);

        [PreserveSig]
        int Clear();
    }

    [ComImport]
    [Guid("5FA08F80-2657-458E-AF75-46F73FA6AC5C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensor
    {
        [PreserveSig]
        int GetID(out Guid id);

        [PreserveSig]
        int GetCategory(out Guid category);

        [PreserveSig]
        int GetType(out Guid type);

        [PreserveSig]
        int GetFriendlyName([MarshalAs(UnmanagedType.BStr)] out string? name);

        [PreserveSig]
        int GetProperty([In] ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int GetProperties(nint keys, out nint values);

        [PreserveSig]
        int GetSupportedDataFields(out nint keys);

        [PreserveSig]
        int SetProperties(IPortableDeviceValues properties, out IPortableDeviceValues? results);

        [PreserveSig]
        int SupportsDataField([In] ref PropertyKey key, out short supported);

        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetData(out nint report);

        [PreserveSig]
        int SupportsEvent([In] ref Guid eventGuid, out short supported);

        [PreserveSig]
        int GetEventInterest(out nint values, out uint count);

        [PreserveSig]
        int SetEventInterest(nint values, uint count);

        [PreserveSig]
        int SetEventSink(nint events);
    }

    // ISensorDataReport's two consumed IUnknown slots. Report pointers remain borrowed in events
    // and are released once after GetData; no per-report runtime callable wrapper is created.
    private readonly unsafe struct SensorReport(nint pointer)
    {
        internal int GetTimestamp(out SystemTime value)
        {
            value = default;
            var method = (delegate* unmanaged[Stdcall]<nint, SystemTime*, int>)(*(nint**)pointer)[3];
            fixed (SystemTime* target = &value)
            {
                return method(pointer, target);
            }
        }

        internal int GetSensorValue(ref PropertyKey key, out PropVariant value)
        {
            value = default;
            var method = (delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropVariant*, int>)(*(nint**)pointer)[4];
            fixed (PropertyKey* property = &key)
            fixed (PropVariant* target = &value)
            {
                return method(pointer, property, target);
            }
        }
    }


    [ComImport]
    [Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPortableDeviceValues
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int SetValue([In] ref PropertyKey key, [In] ref PropVariant value);

        [PreserveSig]
        int GetValue([In] ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetStringValue([In] ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);

        [PreserveSig]
        int GetStringValue([In] ref PropertyKey key, out nint value);

        [PreserveSig]
        int SetUnsignedIntegerValue([In] ref PropertyKey key, uint value);
    }

    [ComImport]
    [Guid("77A1C827-FCD2-4689-8915-9D613CC5FA3E")]
    private class SensorManagerClass;

    [ComImport]
    [Guid("0C15D503-D017-47CE-9016-7B3F978721CC")]
    private class PortableDeviceValuesClass;
}

/// <summary>Where <see cref="LegacyMotionSensors" /> looks for a gyrometer and an accelerometer.</summary>
internal enum LegacyMotionSensorSource
{
    /// <summary>The standard 3D gyrometer and accelerometer types WinRT's default sensors wrap.</summary>
    Standard,

    /// <summary>Intel's "Physical Gyrometer" and "Physical Accelerometer" custom sensors, as on the MSI Claw.</summary>
    Physical
}

/// <summary>One IMU report in sensor space, before the device's axis map.</summary>
/// <param name="AngularVelocity">Degrees per second.</param>
/// <param name="Acceleration">In g, or null where the device has no accelerometer.</param>
/// <param name="Timestamp">The report's sensor timestamp.</param>
internal readonly record struct MotionSensorReading(
    Vector3 AngularVelocity,
    Vector3? Acceleration,
    DateTimeOffset Timestamp);

/// <summary>What one poll of the IMU produced.</summary>
internal enum MotionSensorReadResult
{
    /// <summary>A hardware report the caller has not seen before.</summary>
    Fresh,

    /// <summary>The sensor repeated its previous report; nothing to publish.</summary>
    Duplicate,

    /// <summary>The read failed; the reported error names the decisive COM or value fault.</summary>
    Failed
}
