using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Internal.Devices;

internal sealed partial class ProfiledPowerService
{
    private bool IndependentPowerLimits => model?.IndependentPowerLimits == true;

    // The model is fixed for the service's lifetime, so its controls are described once.
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors ??= Describe();

    private IReadOnlyList<CapabilityDescriptor> Describe()
    {
        return model is null
            ? []
            :
            [
                ProviderControls.Integer(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                        DisplayKey.SustainedPowerLimit, model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt,
                        true, false)
                    with
                    {
                        PairedPowerLimitId = CapabilityIds.PowerBoost, PowerPresets = model.PowerPresets
                    },
                ProviderControls.Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                    DisplayKey.BoostPowerLimit, model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true,
                    false),
                .. IndependentPowerLimits
                    ? new[]
                    {
                        ProviderControls.Integer(CapabilityIds.PowerFast, CapabilityRole.PowerFastLimit,
                            DisplayKey.BoostPowerLimit, model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt,
                            true, false) with { Display = CapabilityDescriptors.Label("Fast PPT limit") }
                    }
                    : [],
                .. _paths?.Descriptor is { } path ? new[] { path } : []
            ];
    }

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.PowerSustained, State == DeviceServiceState.Owned,
            PowerObservation(CpuPowerLimit.Sustained), PowerObservationTime(CpuPowerLimit.Sustained), Reason),
        ProviderControls.State(CapabilityIds.PowerBoost, State == DeviceServiceState.Owned,
            PowerObservation(CpuPowerLimit.Slow), PowerObservationTime(CpuPowerLimit.Slow), Reason),
        .. IndependentPowerLimits
            ? new[]
            {
                ProviderControls.State(CapabilityIds.PowerFast,
                    State == DeviceServiceState.Owned && _transport?.SupportsIndependentLimits == true,
                    PowerObservation(CpuPowerLimit.Boost), PowerObservationTime(CpuPowerLimit.Boost), Reason)
            }
            : [],
        .. _paths is { } paths ? new[] { paths.State(State == DeviceServiceState.Owned, Reason) } : []
    ];

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "CPU power access is unavailable.");
        }

        if (command.CapabilityId == CapabilityIds.PowerBackend && _paths is not null)
        {
            return await SelectPathAsync(_paths, command, firmware, token).ConfigureAwait(false);
        }

        var fastOnly = IndependentPowerLimits && command.CapabilityId == CapabilityIds.PowerFast;
        var sustained = 0;
        var boost = 0;
        if (!fastOnly && !DevicePowerPair.TryResolve(command, Descriptors, out sustained, out boost, out var error))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
        }

        var dispatched = false;
        if (IndependentPowerLimits && command.ApplyUniformPowerTarget
            && !DevicePowerPair.Fits(command.RequestedValue!.IntegerValue!.Value,
                Descriptors.Single(descriptor => descriptor.CapabilityId == CapabilityIds.PowerFast)))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "The uniform target is outside the independent fast PPT range.");
        }

        try
        {
            if (IndependentPowerLimits)
            {
                if (_transport?.SupportsIndependentLimits != true)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "Independent SMU power limits are unavailable on this transport.");
                }

                if (fastOnly)
                {
                    await WriteIndependentAsync(CpuPowerLimit.Boost, command.RequestedValue!.IntegerValue!.Value,
                        firmware, token, () => dispatched = true).ConfigureAwait(false);
                }
                else
                {
                    await WriteIndependentAsync(CpuPowerLimit.Sustained, sustained, firmware, token,
                        () => dispatched = true).ConfigureAwait(false);
                    await WriteIndependentAsync(CpuPowerLimit.Slow, boost, firmware, token,
                        () => dispatched = true).ConfigureAwait(false);
                    if (command.ApplyUniformPowerTarget)
                    {
                        await WriteIndependentAsync(CpuPowerLimit.Boost,
                            command.RequestedValue!.IntegerValue!.Value, firmware, token,
                            () => dispatched = true).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                await WriteAsync(new CpuPowerLimits(sustained, boost, boost), firmware, token, () => dispatched = true)
                    .ConfigureAwait(false);
            }
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Switches between PawnIO and KX, re-writing the last limits so MCHBAR follows the new path.</summary>
    private async ValueTask<CapabilityCommandResult> SelectPathAsync(IntelPowerPaths paths, CapabilityCommand command,
        string firmware, CancellationToken token)
    {
        if (!paths.TryParse(command, out var path))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "This power-limit path is not available on this device.");
        }

        if (path == paths.Selected)
        {
            return CommandResults.Applied(command);
        }

        var dispatched = false;
        try
        {
            await paths.SelectAsync(path, firmware, token).ConfigureAwait(false);
            if (_writtenLimits.TryGetValue(CpuPowerLimit.Sustained, out var sustained)
                && _writtenLimits.TryGetValue(CpuPowerLimit.Slow, out var slow)
                && _writtenLimits.TryGetValue(CpuPowerLimit.Boost, out var boost))
            {
                await WriteAsync(new CpuPowerLimits(sustained.Watts, slow.Watts, boost.Watts), firmware, token,
                    () => dispatched = true).ConfigureAwait(false);
            }

            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    private CapabilityValue? PowerObservation(CpuPowerLimit limit)
    {
        return _writtenLimits.TryGetValue(limit, out var written)
            ? CapabilityValue.Integer((int)Math.Round(written.Watts))
            : null;
    }

    private DateTimeOffset? PowerObservationTime(CpuPowerLimit limit)
    {
        return _writtenLimits.TryGetValue(limit, out var written) ? written.At : null;
    }
}

internal sealed partial class ProfiledFanService
{
    // The EC profile is fixed for the service's lifetime, so its controls are described once.
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors ??= Describe();

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.FanMode, State == DeviceServiceState.Owned,
            Mode is null ? null : CapabilityValue.Choice(Mode), ObservedAt, Reason),
        ProviderControls.State(CapabilityIds.FanDuty, State == DeviceServiceState.Owned,
            Duty is { } duty ? CapabilityValue.Integer(duty) : null, ObservedAt, Reason),
        .. !profile.ObserveDuty
            ? Array.Empty<CapabilityState>()
            : new[]
            {
                ProviderControls.State("fan.measured-duty", State == DeviceServiceState.Owned,
                    MeasuredDuty is { } measured ? CapabilityValue.Integer(measured) : null,
                    MeasuredDutyObservedAt, Reason)
            },
        .. profile.RpmHighAddress is null
            ? Array.Empty<CapabilityState>()
            : new[]
            {
                ProviderControls.State(CapabilityIds.FanRpm, State == DeviceServiceState.Owned,
                    Rpm is { } rpm ? CapabilityValue.Integer(rpm) : null, RpmObservedAt, Reason)
            }
    ];

    private IReadOnlyList<CapabilityDescriptor> Describe()
    {
        List<CapabilityDescriptor> descriptors =
        [
            CapabilityDescriptors.Choice(CapabilityIds.FanMode, CapabilityRole.FanMode,
                new CapabilityDisplay { Key = DisplayKey.FanMode },
                CapabilityDescriptors.Choices(["automatic", "custom", "full-speed"]), read: false, write: true),
            ProviderControls.Integer(CapabilityIds.FanDuty, CapabilityRole.FanDuty, DisplayKey.FanSpeed, 0, 100,
                CapabilityUnit.Percent, true, false)
        ];
        if (profile.RpmHighAddress is not null)
        {
            descriptors.Add(ProviderControls.Integer(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                DisplayKey.FanSpeed, 0, 65535, CapabilityUnit.Rpm, false));
        }

        if (profile.ObserveDuty)
        {
            descriptors.Add(ProviderControls.Integer("fan.measured-duty", CapabilityRole.Telemetry,
                DisplayKey.FanSpeed, 0, 100, CapabilityUnit.Percent, false));
        }

        return descriptors;
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Refresh();
        return ValueTask.CompletedTask;
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "EC fan access is unavailable.");
        }

        var value = command.RequestedValue!;
        var dispatched = false;
        try
        {
            await WriteAsync(command.CapabilityId == CapabilityIds.FanMode ? value.ChoiceValue! : "custom",
                command.CapabilityId == CapabilityIds.FanDuty ? value.IntegerValue : null, firmware, token,
                () => dispatched = true).ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }
}

internal sealed partial class ProfiledControllerService
{
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors { get; } =
    [
        CapabilityDescriptors.Ownership(CapabilityIds.Controller, CapabilityRole.ControllerSource,
            new CapabilityDisplay { Key = DisplayKey.Controller }),
        CapabilityDescriptors.Action(CapabilityIds.Rumble, CapabilityRole.HapticSink,
            new CapabilityDisplay { Key = DisplayKey.Rumble })
    ];

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.Controller, Connected,
            CapabilityValue.Choice(Connected ? SourceOwnership.Managed : SourceOwnership.Device), _ownershipAt, Reason),
        ProviderControls.State(CapabilityIds.Rumble, SupportsRumble, null, null, Reason)
    ];

    internal override ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Connected)
        {
            Rumble(frame);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed partial class ProfiledMotionService
{
    private DateTimeOffset? _startedAt;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors { get; } =
    [
        CapabilityDescriptors.Ownership(CapabilityIds.Motion, CapabilityRole.MotionSource,
            new CapabilityDisplay { Key = DisplayKey.Motion })
    ];

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.Motion, State == DeviceServiceState.Owned,
            CapabilityValue.Choice(State == DeviceServiceState.Owned
                ? SourceOwnership.Managed
                : SourceOwnership.Unavailable), _startedAt, Reason)
    ];
}
