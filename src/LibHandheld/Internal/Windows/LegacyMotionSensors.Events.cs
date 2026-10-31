using System.Numerics;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Windows;

// Driver events avoid polling duplicate reports. LegacyMotionStream owns the polling fallback.
internal sealed partial class LegacyMotionSensors
{
    /// <summary><c>SENSOR_EVENT_DATA_UPDATED</c> from sensors.h.</summary>
    private static readonly Guid DataUpdatedEvent = new("2ED0F2A4-0087-41D3-87DB-6773370B3C88");

    private SensorEventSink? _accelerometerSink;
    private bool _eventFailureReported;
    private SensorEventSink? _gyrometerSink;
    private Vector3? _latestAcceleration;

    /// <summary>Starts receiving both sensors' reports as the driver publishes them.</summary>
    /// <param name="onReading">
    ///     Called on a Sensor API thread with every fresh gyrometer report paired with the latest
    ///     accelerometer report. Keep it short and allocation-free; it runs inside the platform's
    ///     event dispatch.
    /// </param>
    /// <param name="error">Why the subscription failed, when it did.</param>
    /// <returns>
    ///     True when the gyrometer and any present accelerometer are subscribed. A failed new registration
    ///     is unwound; a call while already subscribed returns false without removing the existing subscription.
    /// </returns>
    /// <remarks>
    ///     With an accelerometer, the first gyrometer reports before its first report are dropped: the
    ///     offset calibrator needs the acceleration to recognise rest, and a reading with a fabricated
    ///     acceleration would teach it a wrong offset. Without one, gyrometer reports go out alone.
    /// </remarks>
    public bool TrySubscribe(Action<MotionSensorReading> onReading, out string? error)
    {
        ArgumentNullException.ThrowIfNull(onReading);
        error = null;
        ISensor gyrometer;
        ISensor? accelerometer;
        SensorEventSink gyrometerSink;
        SensorEventSink accelerometerSink;
        lock (_gate)
        {
            if (_gyrometer is null)
            {
                error = "the IMU handles are closed";
                return false;
            }

            if (_gyrometerSink is not null)
            {
                error = "the physical IMU is already subscribed";
                return false;
            }

            gyrometer = _gyrometer;
            accelerometer = _accelerometer;
            gyrometerSink = new SensorEventSink(this, true, onReading);
            accelerometerSink = new SensorEventSink(this, false, onReading);
            _gyrometerSink = gyrometerSink;
            _accelerometerSink = accelerometerSink;
            _latestAcceleration = null;
            _eventFailureReported = false;
        }

        try
        {
            if ((accelerometer is null
                 || TryRegister(accelerometer, accelerometerSink, _profile.AccelerometerName, out error))
                && TryRegister(gyrometer, gyrometerSink, _profile.GyrometerName, out error))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        Unsubscribe();
        return false;
    }

    /// <summary>Stops event delivery. Returns once no callback is still running.</summary>
    public void Unsubscribe()
    {
        SensorEventSink? gyrometerSink;
        SensorEventSink? accelerometerSink;
        ISensor? gyrometer;
        ISensor? accelerometer;
        lock (_gate)
        {
            gyrometerSink = _gyrometerSink;
            accelerometerSink = _accelerometerSink;
            gyrometer = _gyrometer;
            accelerometer = _accelerometer;
            _gyrometerSink = null;
            _accelerometerSink = null;
        }

        // Detached first, and unregistered outside the gate: SetEventSink waits for an in-flight
        // callback, and a callback takes the gate for the counter.
        gyrometerSink?.Detach();
        accelerometerSink?.Detach();
        if (gyrometer is not null)
        {
            Unregister(gyrometer, gyrometerSink, _profile.GyrometerName);
        }

        if (accelerometer is not null)
        {
            Unregister(accelerometer, accelerometerSink, _profile.AccelerometerName);
        }
    }

    private static bool TryRegister(ISensor sensor, SensorEventSink sink, string name, out string? error)
    {
        error = null;
        var events = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        try
        {
            Marshal.StructureToPtr(DataUpdatedEvent, events, false);
            var result = sensor.SetEventInterest(events, 1);
            if (result < 0)
            {
                error = $"{name} SetEventInterest returned 0x{result:X8}";
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(events);
        }

        sink.Pointer = Marshal.GetComInterfaceForObject(sink, typeof(ISensorEvents));
        var sinkResult = sensor.SetEventSink(sink.Pointer);
        if (sinkResult >= 0)
        {
            return true;
        }

        error = $"{name} SetEventSink returned 0x{sinkResult:X8}";
        return false;
    }

    private static void Unregister(ISensor sensor, SensorEventSink? sink, string name)
    {
        if (sink is null || sink.Pointer == 0)
        {
            return;
        }

        try
        {
            var result = sensor.SetEventSink(0);
            if (result < 0)
            {
                DeviceTrace.Warn("motion", $"{name} SetEventSink(null) returned 0x{result:X8}.");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            DeviceTrace.Failure("motion", $"{name} event sink could not be detached", ex);
        }
        finally
        {
            Marshal.Release(sink.Pointer);
            sink.Pointer = 0;
        }
    }

    private void OnReport(bool gyrometer, SensorReport report, Action<MotionSensorReading> publish)
    {
        if (!gyrometer)
        {
            if (!TryReadVector(report, _profile.AccelerometerAxes, out var accelerometerReport,
                    out var accelerationError))
            {
                ReportEventFailure(accelerationError);
                return;
            }

            lock (_gate)
            {
                _latestAcceleration = accelerometerReport;
            }

            return;
        }

        if (!TryReadReportKey(report, out var reportKey, out var timestamp, out var keyError))
        {
            ReportEventFailure(keyError);
            return;
        }

        Vector3? latestAcceleration;
        bool hasAccelerometer;
        lock (_gate)
        {
            if (_lastReportKey == reportKey)
            {
                return;
            }

            _lastReportKey = reportKey;
            latestAcceleration = _latestAcceleration;
            hasAccelerometer = _accelerometer is not null;
        }

        if (hasAccelerometer && latestAcceleration is null)
        {
            return;
        }

        if (!TryReadVector(report, _profile.GyroAxes, out var angularVelocity, out var error))
        {
            ReportEventFailure(error);
            return;
        }

        if (_profile.HasCounter && !TryReadTimestamp(report, out timestamp, out error))
        {
            ReportEventFailure(error);
            return;
        }

        publish(new MotionSensorReading(angularVelocity, latestAcceleration, timestamp));
    }

    /// <summary>Logs the first failed event report per subscription; a stream of them is one fact.</summary>
    private void ReportEventFailure(MotionSensorReadError? error)
    {
        lock (_gate)
        {
            if (_eventFailureReported)
            {
                return;
            }

            _eventFailureReported = true;
        }

        DeviceTrace.Warn("motion", $"IMU event report could not be read: {error}");
    }

    [ComVisible(true)]
    private sealed class SensorEventSink(
        LegacyMotionSensors owner,
        bool gyrometer,
        Action<MotionSensorReading> onReading) : ISensorEvents
    {
        private Action<MotionSensorReading>? _onReading = onReading;

        /// <summary>The COM pointer handed to <c>SetEventSink</c>, zero while unregistered.</summary>
        internal nint Pointer { get; set; }

        public int OnStateChanged(nint sensor, int state)
        {
            return 0;
        }

        public int OnDataUpdated(nint sensor, nint report)
        {
            if (_onReading is not { } publish || report == 0)
            {
                return 0;
            }

            try
            {
                owner.OnReport(gyrometer, new SensorReport(report), publish);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                owner.ReportEventFailure(new MotionSensorReadError(MotionSensorErrorKind.NativeFailure,
                    exception.HResult));
            }

            return 0;
        }

        public int OnEvent(nint sensor, ref Guid eventId, nint eventData)
        {
            return 0;
        }

        public int OnLeave(ref Guid sensorId)
        {
            return 0;
        }

        internal void Detach()
        {
            _onReading = null;
        }
    }

    [ComImport]
    [Guid("5D8DCC91-4641-47E7-B7C3-B74F48A6C391")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISensorEvents
    {
        [PreserveSig]
        int OnStateChanged(nint sensor, int state);

        [PreserveSig]
        int OnDataUpdated(nint sensor, nint report);

        [PreserveSig]
        int OnEvent(nint sensor, [In] ref Guid eventId, nint eventData);

        [PreserveSig]
        int OnLeave([In] ref Guid sensorId);
    }
}
