using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Plugin;
using Microsoft.Win32;

namespace LibHandheld.Families.MsiClaw;

internal sealed class MsiWmiPlatform : IMsiWmiTransport
{
    /// <summary>HC's fixed <c>WmiPath</c>, the instance every Claw class invokes.</summary>
    private const string HcInstancePath = @"MSI_ACPI.InstanceName='ACPI\PNP0C14\0_0'";

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _serializer = new(1, 1);
    private bool _disposed;
    private ManagementObject? _instance;
    private ManagementClass? _packageClass;

    /// <inheritdoc />
    public ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken)
    {
        return RunSerializedAsync(
            () =>
            {
                // The bound instance is kept: a failed call already drops it in RunSerializedAsync.
                using ManagementClass definition = new("root\\WMI", "MSI_ACPI", null);
                if (definition.Methods.Cast<MethodData>().All(method => method.Name != "Get_WMI"))
                {
                    return false;
                }

                return AcquireProvider();
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        if (!methodName.StartsWith("Get_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only MSI_ACPI Get_* methods may use the getter path.", nameof(methodName));
        }

        return RunSerializedAsync(
            () => InvokeCore(methodName, CreatePackage(selector), true),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(package);
        if (!methodName.StartsWith("Set_", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only MSI_ACPI Set_* methods may use the setter path.", nameof(methodName));
        }

        if (package.Length != ClawHardwareFacts.WmiPackageLength)
        {
            throw new ArgumentException("MSI_ACPI writes require exactly 32 bytes.", nameof(package));
        }

        return Complete(RunSerializedAsync(
            () =>
            {
                _ = InvokeCore(methodName, [.. package], false);
                return true;
            },
            cancellationToken));

        static async ValueTask Complete(ValueTask<bool> pending)
        {
            _ = await pending.ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_serializer.Wait(0))
        {
            InvalidateProvider();
            _serializer.Release();
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask<T> RunSerializedAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OperationTimeout);
        await _serializer.WaitAsync(deadline.Token).ConfigureAwait(false);
        Task<T>? operationTask = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            operationTask = Task.Run(operation, CancellationToken.None);
            return await operationTask
                .WaitAsync(deadline.Token)
                .ConfigureAwait(false);
        }
        catch when (operationTask?.IsFaulted == true)
        {
            InvalidateProvider();
            throw;
        }
        finally
        {
            if (operationTask is null || operationTask.IsCompleted)
            {
                if (_disposed)
                {
                    InvalidateProvider();
                }

                _serializer.Release();
            }
            else
            {
                _ = ReleaseSerializerWhenCompleteAsync(operationTask);
            }
        }
    }

    private async Task ReleaseSerializerWhenCompleteAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The caller already received the bounded timeout. Observing the late transport failure
            // here prevents an unobserved exception while retaining serialization until WMI exits.
            InvalidateProvider();
        }
        finally
        {
            if (_disposed)
            {
                InvalidateProvider();
            }

            _serializer.Release();
        }
    }

    /// <param name="methodName">The MSI_ACPI method.</param>
    /// <param name="request">The 32-byte package sent with it.</param>
    /// <param name="requireSuccess">
    ///     True for a getter, whose bytes mean nothing without the success status. A setter's response
    ///     is not checked: HC's <c>WMI.Set</c> ignores it, and firmware that answers a write in another
    ///     shape has still been written.
    /// </param>
    private byte[] InvokeCore(string methodName, byte[] request, bool requireSuccess)
    {
        if (!AcquireProvider())
        {
            throw new FileNotFoundException("The reviewed MSI_ACPI instance was not present.");
        }

        var instance = _instance!;
        var packageClass = _packageClass!;

        // Null means the method takes no in-parameters, which is not an error and not a missing
        // instance: on the A2VM's firmware `Get_WMI`, `Get_EC`, `Get_EC2` and `GetPackage` are all
        // declared `IN[] OUT[Instance Data]`, while every addressed accessor takes the package.
        // Assigning `input["Data"]` unconditionally therefore threw a NullReferenceException on the
        // very first call of the identity check, and the whole device cycle faulted behind it.
        // Device-verified on the reference Claw 2026-08-29: with a null input, `Get_WMI` returns its
        // 32-byte package with status 0x01.
        using var input = instance.GetMethodParameters(methodName);
        using var package = input is null ? null : packageClass.CreateInstance();
        if (input is not null)
        {
            if (package is null)
            {
                throw new IOException("The WMI Package_32 request instance could not be created.");
            }

            package["Bytes"] = request;
            input["Data"] = package;
        }

        using var output = instance.InvokeMethod(methodName, input, null)
                           ?? throw new IOException($"{methodName} returned no response.");
        if (output["Data"] is not ManagementBaseObject returned)
        {
            throw new InvalidDataException($"{methodName} returned no Package_32 response.");
        }

        using (returned)
        {
            if (!requireSuccess)
            {
                return returned["Bytes"] as byte[] ?? [];
            }

            // HC's WMI.Get takes any non-empty response and treats byte 0 as the status.
            if (returned["Bytes"] is not byte[] { Length: > 1 } response)
            {
                throw new InvalidDataException($"{methodName} returned an empty Package_32 response.");
            }

            if (response[0] != 0x01)
            {
                throw new InvalidDataException(
                    $"{methodName} returned status 0x{response[0]:X2} instead of success.");
            }

            return response;
        }
    }

    private bool AcquireProvider()
    {
        if (_instance is not null && _packageClass is not null)
        {
            return true;
        }

        var instance = FindActiveInstance();
        if (instance is null)
        {
            return false;
        }

        ManagementClass? packageClass = null;
        try
        {
            packageClass = new ManagementClass("root\\WMI", "Package_32", null);
            _instance = instance;
            _packageClass = packageClass;
            return true;
        }
        catch
        {
            packageClass?.Dispose();
            instance.Dispose();
            throw;
        }
    }

    private void InvalidateProvider()
    {
        _packageClass?.Dispose();
        _packageClass = null;
        _instance?.Dispose();
        _instance = null;
    }

    /// <summary>
    ///     Binds the instance HC binds, <c>ACPI\PNP0C14\0_0</c>. Only when that path does not resolve
    ///     does it fall back to the first active MSI_ACPI instance.
    /// </summary>
    private static ManagementObject? FindActiveInstance()
    {
        ManagementObject fixedInstance = new("root\\WMI", HcInstancePath, null);
        try
        {
            fixedInstance.Get();
            return fixedInstance;
        }
        catch (ManagementException)
        {
            fixedInstance.Dispose();
        }

        using ManagementObjectSearcher searcher = new(
            "root\\WMI",
            "SELECT * FROM MSI_ACPI WHERE Active = TRUE");
        using var candidates = searcher.Get();
        return candidates.Cast<ManagementObject>().FirstOrDefault();
    }

    private static byte[] CreatePackage(byte selector)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = selector;
        return package;
    }
}

internal sealed partial class WindowsClawIdentityReader : IClawIdentityReader
{
    private readonly Func<DeviceIdentitySnapshot> _readBaseIdentity;
    private readonly Func<IReadOnlyList<UsbEndpointObservation>> _readControllerEndpoints;
    private readonly Func<ushort?> _readMcuRelease;
    private readonly Func<bool> _readOnAcPower;
    private readonly IMsiWmiTransport _wmi;

    public WindowsClawIdentityReader(IMsiWmiTransport wmi)
        : this(wmi, ReadBaseMachineIdentity, ReadControllerEndpoints, ReadLineStatus, ReadMcuRelease)
    {
    }

    internal WindowsClawIdentityReader(
        IMsiWmiTransport wmi,
        Func<DeviceIdentitySnapshot> readBaseIdentity,
        Func<IReadOnlyList<UsbEndpointObservation>> readControllerEndpoints,
        Func<bool> readOnAcPower,
        Func<ushort?> readMcuRelease)
    {
        _wmi = wmi ?? throw new ArgumentNullException(nameof(wmi));
        _readBaseIdentity = readBaseIdentity ?? throw new ArgumentNullException(nameof(readBaseIdentity));
        _readControllerEndpoints = readControllerEndpoints
                                   ?? throw new ArgumentNullException(nameof(readControllerEndpoints));
        _readOnAcPower = readOnAcPower ?? throw new ArgumentNullException(nameof(readOnAcPower));
        _readMcuRelease = readMcuRelease ?? throw new ArgumentNullException(nameof(readMcuRelease));
    }

    /// <inheritdoc />
    public async ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await Task.Run(_readBaseIdentity, CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        // This is the admission boundary. MSI's WMI provider reaches the embedded controller, and
        // the controller inventory reaches HID/PnP. Neither is even queried until SMBIOS names MSI
        // and a baseboard in the model table, the switch HC's IDevice.GetCurrent makes. StartAsync
        // repeats this check, so an incorrectly constructed start context still fails before a
        // transport is touched.
        var model = ClawModels.Find(snapshot);
        if (model is null)
        {
            return new ClawIdentityState
            {
                Snapshot = snapshot,
                ExactMachineMatch = false,
                OnAcPower = false
            };
        }

        // Recorded for diagnostics only; the MCU revision comes from the HID collection below.
        var controllerEndpoints = await Task.Run(
                _readControllerEndpoints,
                CancellationToken.None)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        snapshot = snapshot with { UsbEndpoints = controllerEndpoints };

        var wmiAvailable = false;
        string? ecFirmware = null;
        string? legacyBinding = null;
        string? interfaceVersion = null;
        try
        {
            if (await _wmi.IsProviderAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                wmiAvailable = true;

                // HC calls Get_WMI (block 1) and ignores the answer, and never calls Get_EC. Both are
                // read here for diagnostics and to migrate journal entries earlier builds bound to them:
                // a refusal of either leaves the provider available, as HC would still write.
                var wmiVersion = await TryGetAsync("Get_WMI", 1, cancellationToken).ConfigureAwait(false);
                var ec = await TryGetAsync("Get_EC", 0, cancellationToken).ConfigureAwait(false);
                ecFirmware = ec is null ? null : DecodeEcFirmware(ec);

                // The EC returns its version with its build stamp appended and no separator, so the
                // field reads "1T52EMS1.1091204202509:10:47" (device-observed on the reference Claw,
                // 2026-08-29; the raw response is in docs/device-integration.md). The legacy binding
                // used the version alone, or the BIOS version where the EC's could not be decoded; the
                // snapshot keeps the whole field for remote diagnosis.
                interfaceVersion = wmiVersion is { Length: > 3 } ? $"{wmiVersion[2]}.{wmiVersion[3]}" : "unknown";
                var firmware = EcFirmwareVersion(ecFirmware) is { } ecVersion
                    ? $"ec:{ecVersion}"
                    : snapshot.BiosVersion is { Length: > 0 } bios
                        ? $"bios:{bios}"
                        : "ec:unknown";
                legacyBinding = $"{firmware};msi-acpi:{interfaceVersion}";
            }
        }
        // InvalidDataException is in this list because this class throws it: an invalid Package_32
        // response, a bad status byte, and more than one active MSI_ACPI instance all raise it.
        // Leaving it out meant a malformed provider response escaped WindowsClawIdentityReader,
        // failed plugin startup and eventually faulted all of Device Integration (controller,
        // motion and OEM services included, none of which need WMI) instead of degrading the
        // WMI-backed power and fan capabilities alone.
        catch (Exception ex) when (ex is ManagementException or IOException
                                       or InvalidDataException or UnauthorizedAccessException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A permissions refusal, a missing instance and a malformed response all became this
            // one flag, and all three reached the user as the same partially-available device.
            DeviceTrace.Failure("wmi", "MSI_ACPI provider probe failed", ex);
            wmiAvailable = false;
            legacyBinding = null;
            interfaceVersion = null;
        }

        // The MCU revision is recorded for diagnostics and picks HC's nearest-firmware row; it is never
        // gated on. It was gated to exactly 0229 until MSI shipped 0230 through the controller firmware
        // updater on 2026-09-18, which refused controller ownership and lighting on every updated unit
        // even though nothing the plugin sends had changed. HC reads it as the HID collection's
        // Attributes.Version, as this does.
        string? mcuFirmware = null;
        try
        {
            mcuFirmware = await Task.Run(_readMcuRelease, CancellationToken.None)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false) is { } release
                ? release.ToString("X4", CultureInfo.InvariantCulture)
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            DeviceTrace.Failure("mcu", "The MCU revision could not be read; HC's reference row applies", ex);
        }

        snapshot = snapshot with
        {
            EcFirmwareVersion = ecFirmware,
            McuFirmwareVersion = mcuFirmware,
            WmiProviderSignatures = wmiAvailable
                ? ["root\\WMI:MSI_ACPI", "root\\WMI:MSI_ACPI.Get_WMI:" + interfaceVersion]
                : []
        };

        var onAcPower = await Task.Run(ReadOnAcPower, CancellationToken.None)
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ClawIdentityState
        {
            Snapshot = snapshot,
            ExactMachineMatch = true,
            Model = model,
            WmiAvailable = wmiAvailable,
            LegacyRecoveryBinding = legacyBinding,
            OnAcPower = onAcPower
        };
    }

    /// <inheritdoc />
    public bool ReadOnAcPower()
    {
        try
        {
            return _readOnAcPower();
        }
        catch (Exception ex) when (ex is ManagementException or IOException
                                       or InvalidDataException or UnauthorizedAccessException or COMException
                                       or Win32Exception)
        {
            DeviceTrace.Failure("power", "AC-power observation failed", ex);
            return false;
        }
    }

    private async ValueTask<byte[]?> TryGetAsync(string method, byte selector, CancellationToken cancellationToken)
    {
        try
        {
            return await _wmi.InvokeGetterAsync(method, selector, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            DeviceTrace.Failure("wmi", $"{method} did not answer; recorded as unknown", ex);
            return null;
        }
    }

    /// <summary>The EC version without the build stamp the firmware appends (MMddyyyyHH:mm:ss).</summary>
    /// <param name="field">Raw EC version field, or null when the diagnostic read was unavailable.</param>
    /// <returns>The prefix before a recognized trailing build stamp, the original text without one, or null.</returns>
    internal static string? EcFirmwareVersion(string? field)
    {
        if (field is null)
        {
            return null;
        }

        var match = EcBuildStamp().Match(field);
        return match.Success && match.Index > 0 ? field[..match.Index] : field;
    }

    [GeneratedRegex(@"\d{10}:\d{2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex EcBuildStamp();

    private static DeviceIdentitySnapshot ReadBaseMachineIdentity()
    {
        using var system = QuerySingle(
            "root\\CIMV2",
            "SELECT Manufacturer, Model, SystemSKUNumber, SystemFamily FROM Win32_ComputerSystem");
        using var board = QuerySingle(
            "root\\CIMV2",
            "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
        using var bios = QuerySingle(
            "root\\CIMV2",
            "SELECT SMBIOSBIOSVersion FROM Win32_BIOS");

        return new DeviceIdentitySnapshot
        {
            SystemManufacturer = Normalize(system["Manufacturer"]),
            SystemProduct = Normalize(system["Model"]),
            SystemSku = Normalize(system["SystemSKUNumber"]),
            SystemFamily = Normalize(system["SystemFamily"]),
            BaseboardManufacturer = Normalize(board["Manufacturer"]),
            BaseboardProduct = Normalize(board["Product"]),
            BaseboardVersion = Normalize(board["Version"]),
            BiosVersion = Normalize(bios["SMBIOSBIOSVersion"]),
            UsbEndpoints = []
        };
    }

    private static IReadOnlyList<UsbEndpointObservation> ReadControllerEndpoints()
    {
        var endpoints = new List<UsbEndpointObservation>();
        using ManagementObjectSearcher searcher = new(
            @"root\CIMV2",
            @"SELECT DeviceID, HardwareID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\VID_0DB0&PID_19%' ");
        using var candidates = searcher.Get();
        foreach (var item in candidates.Cast<ManagementObject>())
        {
            using (item)
            {
                var id = Convert.ToString(item["DeviceID"], CultureInfo.InvariantCulture) ?? string.Empty;
                var vendorId = ExtractHex(id, "VID_");
                var productId = ExtractHex(id, "PID_");
                var release = (item["HardwareID"] as string[])
                    ?.Select(value => ExtractHex(value, "REV_"))
                    .FirstOrDefault(value => value is not null);
                if (vendorId is null || productId is null || !IsControllerProduct(productId))
                {
                    continue;
                }

                endpoints.Add(new UsbEndpointObservation
                {
                    VendorId = vendorId,
                    ProductId = productId,
                    DeviceRelease = release
                });
            }
        }

        return endpoints;
    }

    private static ManagementObject QuerySingle(string scope, string query)
    {
        using ManagementObjectSearcher searcher = new(scope, query);
        using var candidates = searcher.Get();
        return candidates.Cast<ManagementObject>().FirstOrDefault()
               ?? throw new FileNotFoundException($"Inventory query returned no rows: {query}");
    }

    /// <summary>The AC line status, as the Ally reads it; cheap enough to read before every command.</summary>
    /// <remarks>An unknown line status counts as AC, as a machine without a battery reported before.</remarks>
    private static bool ReadLineStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return status.AcLineStatus != 0;
    }

    /// <summary>The MCU collection's HID release number, HC's <c>Attributes.Version</c>.</summary>
    private static ushort? ReadMcuRelease()
    {
        return HidEndpointEnumerator.FindMcu()?.ReleaseNumber;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    private static string? Normalize(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? ExtractHex(string value, string marker)
    {
        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || value.Length < start + marker.Length + 4)
        {
            return null;
        }

        var result = value.Substring(start + marker.Length, 4);
        return result.All(Uri.IsHexDigit) ? result.ToUpperInvariant() : null;
    }

    private static bool IsControllerProduct(string value)
    {
        return ushort.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id)
               && id is ClawHardwareFacts.XInputProductId or ClawHardwareFacts.DirectInputProductId;
    }

    private static string? DecodeEcFirmware(byte[] response)
    {
        var marker = Array.IndexOf(response, (byte)0x81, 1);
        if (marker < 0 || marker + 1 >= response.Length)
        {
            return null;
        }

        var end = Array.IndexOf(response, (byte)0, marker + 1);
        if (end < 0)
        {
            end = response.Length;
        }

        var value = Encoding.ASCII.GetString(response, marker + 1, end - marker - 1).Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}

internal sealed class MsiOemEventSource : IMsiOemEventSource
{
    private readonly Lock _gate = new();
    private Func<byte, DateTimeOffset, ValueTask>? _callback;
    private ManagementEventWatcher? _watcher;

    /// <inheritdoc />
    public async ValueTask EnsureEventClassAsync(CancellationToken cancellationToken)
    {
        try
        {
            await MsiEventRepair.EnsureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            // The repair is HC's best effort; without it the buttons arrive only as keyboard chords.
            DeviceTrace.Failure("wmi", "MSI_Event repair failed", ex);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Subscribe(callback));
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_watcher is null)
            {
                return ValueTask.CompletedTask;
            }

            _watcher.EventArrived -= OnEventArrived;
            try
            {
                _watcher.Stop();
            }
            catch (ManagementException)
            {
                // The subscription can disappear with the provider during teardown. Disposal is the
                // terminal operation and no hardware state depends on the watcher.
            }

            _watcher.Dispose();
            _watcher = null;
            _callback = null;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private bool Subscribe(Func<byte, DateTimeOffset, ValueTask> callback)
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return true;
            }

            _callback = callback;
            _watcher = new ManagementEventWatcher("root\\WMI", "SELECT * FROM MSI_Event");
            _watcher.EventArrived += OnEventArrived;
            try
            {
                _watcher.Start();
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Returning false here takes the whole OEM-events service to Passive. Without this
                // line the log showed a device that was partially available and never why.
                DeviceTrace.Failure("wmi", "MSI_Event subscription could not start", ex);
                _watcher.EventArrived -= OnEventArrived;
                _watcher.Dispose();
                _watcher = null;
                _callback = null;
                return false;
            }
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs args)
    {
        var callback = _callback;
        if (callback is null)
        {
            return;
        }

        var raw = args.NewEvent.Properties["MSIEvt"]?.Value;
        if (raw is null)
        {
            return;
        }

        // HC: Convert.ToInt32(MSIEvt) & 0xFF, which also takes a negative signed value.
        var code = unchecked((byte)(Convert.ToInt64(raw, CultureInfo.InvariantCulture) & 0xFF));
        var publication = callback(code, DateTimeOffset.UtcNow).AsTask();
        if (!publication.IsCompletedSuccessfully)
        {
            _ = ObservePublicationAsync(publication);
        }
    }

    private static async Task ObservePublicationAsync(Task publication)
    {
        try
        {
            await publication.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // WSGM owns diagnostics for a rejected publication. An OEM event must not tear
            // down the WMI callback thread or leave an unobserved task exception behind.
        }
    }
}

/// <summary>
///     HC's <c>StartWatching</c> repair for a missing <c>MSI_Event</c> class: point the WMI ACPI
///     driver at MSI's MOF library, restart <c>ACPI\PNP0C14</c>, and wait up to five seconds.
/// </summary>
/// <remarks>
///     HC ships <c>msiapcfg.dll</c> and copies it to <c>SysWOW64</c>. It is MSI's binary and cannot be
///     redistributed here, so the repair runs only where MSI Center already installed it. The registry
///     write and the device restart need elevation, which WSGM has as a shell; without it the repair is
///     logged and skipped, and the buttons stay unavailable as before.
/// </remarks>
internal static class MsiEventRepair
{
    private const string WmiAcpiKey = @"SYSTEM\CurrentControlSet\Services\WmiAcpi";
    private const string MofImagePath = "MofImagePath";

    public static async ValueTask EnsureAsync(CancellationToken cancellationToken)
    {
        if (ClassExists())
        {
            return;
        }

        var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64",
            "msiapcfg.dll");
        if (!File.Exists(library))
        {
            DeviceTrace.Warn("wmi", "MSI_Event is missing and msiapcfg.dll is not installed; the Claw and QS buttons "
                                    + "arrive only as the firmware's keyboard chords.");
            return;
        }

        try
        {
            using (var key = Registry.LocalMachine.CreateSubKey(WmiAcpiKey, true))
            {
                if (!string.Equals(key.GetValue(MofImagePath) as string, library, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(MofImagePath, library, RegistryValueKind.String);
                    DeviceTrace.Info("wmi", $"Set WmiAcpi\\{MofImagePath} to {library}.");
                }
            }

            foreach (var instance in AcpiWmiInstances())
            {
                if (await RestartDeviceAsync(instance, cancellationToken).ConfigureAwait(false))
                {
                    DeviceTrace.Info("wmi", $"Restarted {instance} to load MSI_Event.");
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException
                                       or ManagementException or Win32Exception)
        {
            DeviceTrace.Failure("wmi", "MSI_Event repair could not run", ex);
            return;
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!ClassExists() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ClassExists()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "root\\WMI",
                "SELECT * FROM meta_class WHERE __class = 'MSI_Event'");
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch (ManagementException)
        {
            return false;
        }
    }

    private static List<string> AcpiWmiInstances()
    {
        using ManagementObjectSearcher searcher = new(
            "root\\CIMV2",
            @"SELECT PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'ACPI\\PNP0C14%'");
        using var results = searcher.Get();
        return
        [
            .. results.Cast<ManagementObject>()
                .Select(item =>
                {
                    using (item)
                    {
                        return item["PNPDeviceID"] as string;
                    }
                })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
        ];
    }

    /// <summary>HC's <c>PnPUtil.RestartDevice</c>: <c>pnputil /restart-device</c>.</summary>
    private static async ValueTask<bool> RestartDeviceAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            ArgumentList = { "/restart-device", instanceId },
            CreateNoWindow = true,
            UseShellExecute = false
        });
        if (process is null)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Left to finish: killing a device restart midway is worse than waiting it out unobserved.
            DeviceTrace.Warn("wmi", $"pnputil did not finish restarting {instanceId} within 10 s.");
            return false;
        }

        return process.ExitCode == 0;
    }
}
