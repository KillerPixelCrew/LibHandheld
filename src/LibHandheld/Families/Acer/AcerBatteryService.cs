using System.Globalization;
using System.Management;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Acer's battery health switch, without invoking calibration or the Care Center service.</summary>
/// <remarks>
///     The root\WMI BatteryControl provider exposes one optimized-charging boolean for battery 1,
///     function bit 0. This is the OEM protection mode, not an arbitrary percentage ceiling, charging
///     bypass or a calibration cycle. It persists in the device, so the service journals a readable
///     original before changing it and restores that original on normal release.
/// </remarks>
internal sealed class AcerBatteryService(ProfiledFeatureContext context) : ProfiledFeatureService("acer-battery")
{
    internal const string ProtectionId = "battery.charge-protection";
    private static readonly IReadOnlyList<CapabilityDescriptor> Controls =
    [
        new()
        {
            CapabilityId = ProtectionId, Role = CapabilityRole.ChargeProtectionMode,
            Display = CapabilityDescriptors.Label("Optimized battery charging"),
            ValueKind = CapabilityValueKind.Boolean, SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent
        }
    ];
    private readonly ObservedValues _observed = new();
    private ManagementObject? _wmi;
    private bool _supported = true;
    private bool _unknownOriginalChanged;
    private bool _visibilityDecided;
    private bool _restoreUnverified;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _supported ? Controls : [];
    internal override IReadOnlyList<CapabilityState> States => _supported
        ? [ProviderControls.State(ProtectionId, State == DeviceServiceState.Owned,
            _observed.Value(ProtectionId), _observed.Time(ProtectionId), Reason)] : [];
    public override bool Suspendable => true;

    /// <summary>Opens BatteryControl, reconciles saved state, and decides firmware support once per service lifetime.</summary>
    /// <remarks>An observation failure leaves value unknown and does not itself hide or prohibit the switch.</remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _wmi = WmiProvider.TryOpen("root\\WMI", "SELECT * FROM BatteryControl");
        if (_wmi is null)
        {
            return Set(DeviceServiceState.Passive, Missing("Acer BatteryControl WMI is unavailable."));
        }

        await context.Journal.ReconcileAsync<AcerBatteryOriginal>(ServiceId, context.Firmware,
            RestoreAsync, token).ConfigureAwait(false);
        try
        {
            var status = Read();
            if (!_visibilityDecided)
            {
                _supported = status.Supported;
            }
            _observed.Read(ProtectionId, status.Enabled is { } enabled ? CapabilityValue.Boolean(enabled) : null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"Charge protection observation unavailable: {exception.Message}");
        }

        _visibilityDecided = true;

        return Set(_supported ? DeviceServiceState.Owned : DeviceServiceState.Passive,
            _supported ? null : Missing("Firmware does not declare battery health control."));
    }

    /// <summary>Queries battery 1/function 1 and decodes the provider's two-byte status plus five function bytes.</summary>
    /// <remarks>
    ///     Both uReturn bytes must be zero. uFunctionList bit 0 declares support; uFunctionStatus[0]
    ///     gives 0/1 disabled/enabled. A supported but non-boolean value remains unknown rather than false.
    /// </remarks>
    private (bool Supported, bool? Enabled) Read()
    {
        using var input = _wmi!.GetMethodParameters("GetBatteryHealthControlStatus");
        input["uBatteryNo"] = (byte)1;
        input["uFunctionQuery"] = (byte)1;
        input["uReserved"] = new byte[2];
        using var output = WmiProvider.Invoke(_wmi, "GetBatteryHealthControlStatus", input);
        if (output?["uReturn"] is not byte[] status || status.Length != 2 || status[0] != 0 || status[1] != 0
            || output["uFunctionStatus"] is not byte[] functions || functions.Length != 5
            || output["uFunctionList"] is not byte functionList)
        {
            throw new IOException("Acer battery health response is unavailable or rejected.");
        }

        var supported = (functionList & 1) != 0;
        return (supported, supported && functions[0] <= 1 ? functions[0] == 1 : null);
    }

    /// <summary>Sets function-mask bit 0 for battery 1; zero uint16 uReturn is firmware acknowledgement.</summary>
    /// <remarks>Reserved fields are zero as in the vendor call. No calibration or smart-charge function bits are sent.</remarks>
    private void Write(bool enabled, CancellationToken token, Action? dispatched = null)
    {
        using var input = _wmi!.GetMethodParameters("SetBatteryHealthControl");
        input["uBatteryNo"] = (byte)1;
        input["uFunctionMask"] = (byte)1;
        input["uFunctionStatus"] = enabled ? (byte)1 : (byte)0;
        input["uReservedIn"] = new byte[5];
        token.ThrowIfCancellationRequested();
        dispatched?.Invoke();
        using var output = WmiProvider.Invoke(_wmi, "SetBatteryHealthControl", input);
        if (output?["uReturn"] is not { } result || Convert.ToUInt16(result, CultureInfo.InvariantCulture) != 0)
        {
            throw new IOException("Acer battery health write was not acknowledged; effect unknown.");
        }
    }

    /// <summary>Retains the first available original, dispatches one boolean write, and publishes the accepted value.</summary>
    /// <remarks>
    ///     Readback is not a write gate. If the original is unavailable, control still works but exact
    ///     restoration cannot be claimed. A failure after dispatch forgets the value and is not retried.
    /// </remarks>
    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_wmi is null || State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "Acer battery health control is unavailable.");
        }

        var dispatched = false;
        try
        {
            if (command.RequestedValue?.BooleanValue is not { } enabled)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    "Charge protection requires an on/off value.");
            }

            var recovery = await context.Journal.BeginAsync<AcerBatteryOriginal>(ServiceId, context.Firmware,
                () => Read().Enabled is { } original ? new AcerBatteryOriginal { Enabled = original } : null, token)
                .ConfigureAwait(false);
            _unknownOriginalChanged |= recovery is null;
            Write(enabled, token, () => dispatched = true);
            _observed.Written(ProtectionId, CapabilityValue.Boolean(enabled));
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Forget(ProtectionId);
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Writes the retained protection bit once and records matched, mismatched or unavailable readback.</summary>
    private async ValueTask RestoreAsync(AcerBatteryOriginal original, CancellationToken token)
    {
        Write(original.Enabled, token);
        bool? verified = null;
        try
        {
            verified = Read().Enabled is { } enabled ? enabled == original.Enabled : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"Restored protection observation unavailable: {exception.Message}");
        }

        _restoreUnverified |= verified != true;
        await context.Journal.SetStatusAsync(ServiceId, DeviceRecoveryJournal.RestoreStatus(verified),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Restores a captured original before closing WMI; unknown originals remain ReleasedUnverified.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            if (_wmi is not null)
            {
                await context.Journal.ReconcileAsync<AcerBatteryOriginal>(ServiceId, context.Firmware,
                    RestoreAsync, token).ConfigureAwait(false);
            }

            return Set(_unknownOriginalChanged || _restoreUnverified || context.Journal.HasUnrestoredMutation(ServiceId)
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the provider only; ReleaseAsync owns restoration of this device-persistent setting.</summary>
    internal override ValueTask DisposeAsync()
    {
        _wmi?.Dispose();
        _wmi = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>The firmware-bound optimized-charging bit captured before the first change.</summary>
internal sealed record AcerBatteryOriginal
{
    public bool Enabled { get; init; }
}
