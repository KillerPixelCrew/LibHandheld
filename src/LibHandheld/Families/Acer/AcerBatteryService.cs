using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>Acer native charge thresholds, with the OEM optimized-charging switch on older firmware.</summary>
/// <remarks>
///     The root\WMI BatteryControl provider exposes one optimized-charging boolean for battery 1,
///     function bit 0. Native EC command 0B03 supplies a percentage ceiling and a 50% resume threshold.
///     The native control disables the separate optimized mode while it owns charging. Both originals
///     are retained and restored on release; calibration and charging bypass are never requested.
/// </remarks>
internal sealed class AcerBatteryService(ProfiledFeatureContext context, IAcerLease<IAcerEcTransport> ec,
    bool initializeUnknownValues = false, Func<IAcerBatteryWmi?>? openWmi = null)
    : ProfiledFeatureService("acer-battery")
{
    private static readonly IReadOnlyList<CapabilityDescriptor> Controls =
    [
        CapabilityDescriptors.Boolean(CapabilityIds.ChargeProtection, CapabilityRole.ChargeProtectionMode,
            CapabilityDescriptors.Label("Optimized battery charging"), read: true,
            persistence: CapabilityPersistence.DevicePersistent)
    ];
    private static readonly IReadOnlyList<CapabilityDescriptor> BoundaryControls =
    [
        Controls[0],
        CapabilityDescriptors.Integer(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit,
            CapabilityDescriptors.Label("Custom charge limit"), minimum: 51, maximum: 100, CapabilityUnit.Percent,
            read: true, write: true, CapabilityPersistence.DevicePersistent)
    ];
    private readonly ObservedValues _observed = new();
    private bool _initializationAttempted;
    private IAcerBatteryWmi? _wmi;
    private bool _supported = true;
    private bool _unknownOriginalChanged;
    private bool _visibilityDecided;
    private IAcerEcTransport? _ec;
    private bool _boundarySupported;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _supported
        ? _boundarySupported ? BoundaryControls : Controls : [];
    internal override IReadOnlyList<CapabilityState> States => Descriptors.Select(control =>
        ProviderControls.Observed(control, _observed, State == DeviceServiceState.Owned, Reason)).ToArray();
    public override bool Suspendable => true;

    /// <summary>Opens BatteryControl, reconciles saved state, and decides firmware support once per service lifetime.</summary>
    /// <remarks>An observation failure leaves value unknown and does not itself hide or prohibit the switch.</remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!initializeUnknownValues || !_initializationAttempted)
        {
            _observed.Clear();
        }
        _wmi = (openWmi ?? AcerBatteryWmi.TryOpen)();
        if (_wmi is null)
        {
            return Set(DeviceServiceState.Passive, Missing("Acer BatteryControl WMI is unavailable."));
        }

        _ec = ec.Acquire();
        if (!_visibilityDecided && _ec is not null)
        {
            try
            {
                // An acknowledged 0B03 query establishes this lane. FF fields mean unset, not unsupported.
                _ = ReadBoundary(token);
                _boundarySupported = true;
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
            {
                DeviceTrace.Warn(ServiceId, $"Native charge boundary unavailable: {exception.Message}");
            }
        }

        await context.Journal.ReconcileAsync<AcerBatteryOriginal>(ServiceId, context.Firmware,
            RestoreAsync, token).ConfigureAwait(false);
        try
        {
            var status = _wmi.Read();
            if (!_visibilityDecided)
            {
                _supported = _boundarySupported || status.Supported;
            }
            _observed.Read(CapabilityIds.ChargeProtection,
                status.Enabled is { } enabled ? CapabilityValue.Boolean(enabled) : null);
            if (_boundarySupported)
            {
                var boundary = ReadBoundary(token);
                _observed.Read(CapabilityIds.ChargeLimit, boundary is null ? null
                    : CapabilityValue.Integer(boundary.Enabled ? boundary.Upper : 100));
            }
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            DeviceTrace.Warn(ServiceId, $"Charge protection observation unavailable: {exception.Message}");
        }

        _visibilityDecided = true;

        if (_supported && initializeUnknownValues && !_initializationAttempted)
        {
            _initializationAttempted = true;
            if (_observed.Value(CapabilityIds.ChargeProtection) is null)
            {
                try
                {
                    _wmi.Write(enabled: false, token);
                    _observed.Written(CapabilityIds.ChargeProtection, CapabilityValue.Boolean(false));
                    DeviceTrace.Write(DeviceTraceLevel.Info, ServiceId, "Initial optimized charging: set to Off.");
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    DeviceTrace.Warn(ServiceId, $"Initial optimized charging remains unknown; not retried: {exception.Message}");
                }
            }
            if (_boundarySupported && _observed.Value(CapabilityIds.ChargeLimit) is null)
            {
                try
                {
                    // Initialize the unset native ceiling without changing a readable optimized-charging switch.
                    var boundary = new AcerChargeBoundary(false, 100, AcerChargeBoundary.ResumePercent);
                    _ec!.Exchange(boundary.Packet(), token);
                    _observed.Written(CapabilityIds.ChargeLimit, CapabilityValue.Integer(100));
                    DeviceTrace.Write(DeviceTraceLevel.Info, ServiceId, "Initial custom charge limit: set to 100 percent (no native ceiling).");
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    DeviceTrace.Warn(ServiceId, $"Initial custom charge limit remains unknown; not retried: {exception.Message}");
                }
            }
        }

        return Set(_supported ? DeviceServiceState.Owned : DeviceServiceState.Passive,
            _supported ? null : Missing("Firmware does not declare battery health control."));
    }

    private AcerChargeBoundary? ReadBoundary(CancellationToken token)
    {
        return AcerChargeBoundary.Read(_ec!.Exchange(AcerChargeBoundary.Query(), token));
    }

    /// <summary>Retains the original charging policy and publishes each accepted explicit charging change.</summary>
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
            if (_boundarySupported && command.CapabilityId == CapabilityIds.ChargeLimit)
            {
                if (command.RequestedValue?.IntegerValue is not { } percent || percent is < 51 or > 100)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                        "Charge limit must be 51 through 100 percent; charging resumes at 50 percent.");
                }
                if (_ec is null)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "Acer charge-boundary transport is unavailable.");
                }
                var original = await context.Journal.BeginAsync<AcerBatteryOriginal>(ServiceId, context.Firmware,
                    () => _wmi.Read().Enabled is { } protection ? new AcerBatteryOriginal
                    {
                        Enabled = protection, UsesBoundary = true, Boundary = ReadBoundary(token)
                    } : null, token).ConfigureAwait(false);
                _unknownOriginalChanged |= original?.Entry.OriginalState.Boundary is null;
                // The OEM optimized mode can impose its own ceiling. The explicit percentage owns charging.
                _wmi.Write(enabled: false, token, () => dispatched = true);
                var boundary = new AcerChargeBoundary(percent < 100, checked((byte)percent),
                    AcerChargeBoundary.ResumePercent);
                _ec.Exchange(boundary.Packet(), token, () => dispatched = true);
                _observed.Written(CapabilityIds.ChargeProtection, CapabilityValue.Boolean(false));
                _observed.Written(CapabilityIds.ChargeLimit, CapabilityValue.Integer(percent));
                return CommandResults.Applied(command);
            }
            if (command.RequestedValue?.BooleanValue is not { } enabled)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    "Charge protection requires an on/off value.");
            }

            var recovery = await context.Journal.BeginAsync<AcerBatteryOriginal>(ServiceId, context.Firmware,
                () => _wmi.Read().Enabled is { } original ? new AcerBatteryOriginal
                {
                    Enabled = original, UsesBoundary = _boundarySupported,
                    Boundary = _boundarySupported ? ReadBoundary(token) : null
                } : null, token)
                .ConfigureAwait(false);
            _unknownOriginalChanged |= recovery is null;
            if (_boundarySupported && enabled)
            {
                _unknownOriginalChanged |= recovery?.Entry.OriginalState.Boundary is null;
                _ec!.Exchange(new AcerChargeBoundary(false, 100, AcerChargeBoundary.ResumePercent).Packet(), token,
                    () => dispatched = true);
                _observed.Written(CapabilityIds.ChargeLimit, CapabilityValue.Integer(100));
            }
            _wmi.Write(enabled, token, () => dispatched = true);
            _observed.Written(CapabilityIds.ChargeProtection, CapabilityValue.Boolean(enabled));
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Forget(CapabilityIds.ChargeProtection);
                _observed.Forget(CapabilityIds.ChargeLimit);
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Writes the retained protection bit once; the accepted write completes the restore.</summary>
    private async ValueTask RestoreAsync(AcerBatteryOriginal original, CancellationToken token)
    {
        if (original.UsesBoundary && original.Boundary is { } boundary)
        {
            _ec!.Exchange(boundary.Packet(), token);
        }
        _wmi!.Write(original.Enabled, token);
        await context.Journal.CompleteRestoreAsync(ServiceId,
            () =>
            {
                if (_wmi.Read().Enabled is not { } enabled)
                {
                    return null;
                }
                if (original.UsesBoundary)
                {
                    return original.Boundary is { } expected && ReadBoundary(token) is { } actual
                        ? enabled == original.Enabled && expected == actual : null;
                }
                return enabled == original.Enabled;
            }).ConfigureAwait(false);
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

            return Set(_unknownOriginalChanged || context.Journal.HasUnrestoredMutation(ServiceId)
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Disposes the provider only; ReleaseAsync owns restoration of this device-persistent setting.</summary>
    internal override async ValueTask DisposeAsync()
    {
        _wmi?.Dispose();
        _wmi = null;
        if (_ec is not null)
        {
            _ec = null;
            await ec.ReleaseAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>The firmware-bound optimized-charging bit captured before the first change.</summary>
internal sealed record AcerBatteryOriginal
{
    public bool Enabled { get; init; }

    public bool UsesBoundary { get; init; }

    public AcerChargeBoundary? Boundary { get; init; }
}
