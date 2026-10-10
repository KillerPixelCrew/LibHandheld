using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Internal.Devices;

internal sealed partial class ProfiledPowerService
{
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => model is null
        ? []
        :
        [
            ProviderControls.Integer(CapabilityIds.PowerSustained, CapabilityRole.PowerSustainedLimit,
                    DisplayKey.SustainedPowerLimit, model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true,
                    false)
                with
                {
                    PairedPowerLimitId = CapabilityIds.PowerBoost, PowerPresets = model.PowerPresets
                },
            ProviderControls.Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                DisplayKey.BoostPowerLimit, model.MinimumWatts, model.MaximumWatts, CapabilityUnit.Watt, true, false)
        ];

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.PowerSustained, State == DeviceServiceState.Owned,
            Observed is { } limits ? CapabilityValue.Integer((int)Math.Round(limits.SustainedWatts)) : null, ObservedAt,
            Reason),
        ProviderControls.State(CapabilityIds.PowerBoost, State == DeviceServiceState.Owned,
            Observed is { } slowLimits ? CapabilityValue.Integer((int)Math.Round(slowLimits.SlowWatts)) : null,
            ObservedAt, Reason)
    ];

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "CPU power access is unavailable.");
        }

        if (!DevicePowerPair.TryResolve(command, Descriptors, out var sustained, out var boost, out var error))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
        }

        var dispatched = false;
        try
        {
            await WriteAsync(new CpuPowerLimits(sustained, boost, boost), firmware, token, () => dispatched = true)
                .ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }
}

internal sealed partial class ProfiledFanService
{
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors
    {
        get
        {
            List<CapabilityDescriptor> descriptors =
            [
                new()
                {
                    CapabilityId = CapabilityIds.FanMode, Role = CapabilityRole.FanMode,
                    ValueKind = CapabilityValueKind.Choice,
                    Display = new CapabilityDisplay { Key = DisplayKey.FanMode },
                    SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
                    Choices = new[] { "automatic", "custom", "full-speed" }.Select(value => new CapabilityChoice(value,
                        new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = value })).ToArray()
                },
                ProviderControls.Integer("fan.duty", CapabilityRole.FanDuty, DisplayKey.FanSpeed, 0, 100,
                    CapabilityUnit.Percent, true, false)
            ];
            if (profile.RpmHighAddress is not null)
            {
                descriptors.Add(ProviderControls.Integer(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm,
                    DisplayKey.FanSpeed, 0, 65535, CapabilityUnit.Rpm, false));
            }

            return descriptors;
        }
    }

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.FanMode, State == DeviceServiceState.Owned,
            Mode is null ? null : CapabilityValue.Choice(Mode), ObservedAt, Reason),
        ProviderControls.State("fan.duty", State == DeviceServiceState.Owned,
            Duty is { } duty ? CapabilityValue.Integer(duty) : null, ObservedAt, Reason),
        .. profile.RpmHighAddress is null
            ? Array.Empty<CapabilityState>()
            : new[]
            {
                ProviderControls.State(CapabilityIds.FanRpm, State == DeviceServiceState.Owned,
                    Rpm is { } rpm ? CapabilityValue.Integer(rpm) : null, RpmObservedAt, Reason)
            }
    ];

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
                command.CapabilityId == "fan.duty" ? value.IntegerValue : null, firmware, token,
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
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
    [
        ProviderControls.Ownership(CapabilityIds.Controller, CapabilityRole.ControllerSource, DisplayKey.Controller),
        new()
        {
            CapabilityId = CapabilityIds.Rumble, Role = CapabilityRole.HapticSink,
            ValueKind = CapabilityValueKind.None, Display = new CapabilityDisplay { Key = DisplayKey.Rumble },
            SupportsAction = true, Persistence = CapabilityPersistence.Volatile
        }
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

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors =>
        [ProviderControls.Ownership(CapabilityIds.Motion, CapabilityRole.MotionSource, DisplayKey.Motion)];

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityIds.Motion, State == DeviceServiceState.Owned,
            CapabilityValue.Choice(State == DeviceServiceState.Owned
                ? SourceOwnership.Managed
                : SourceOwnership.Unavailable), _startedAt, Reason)
    ];
}
