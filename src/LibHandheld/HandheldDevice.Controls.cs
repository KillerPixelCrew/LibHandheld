using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using CapabilityChoice = LibHandheld.Internal.Capabilities.CapabilityChoice;
using CapabilityCommand = LibHandheld.Contracts.CapabilityCommand;
using CapabilityCommandResult = LibHandheld.Contracts.CapabilityCommandResult;
using CapabilityDescriptor = LibHandheld.Contracts.CapabilityDescriptor;
using CapabilityDescriptorSet = LibHandheld.Contracts.CapabilityDescriptorSet;
using CapabilityPersistence = LibHandheld.Internal.Capabilities.CapabilityPersistence;
using CapabilityReason = LibHandheld.Contracts.CapabilityReason;
using CapabilityReasonCode = LibHandheld.Contracts.CapabilityReasonCode;
using CapabilityRole = LibHandheld.Contracts.CapabilityRole;
using CapabilityState = LibHandheld.Contracts.CapabilityState;
using CapabilityUnit = LibHandheld.Contracts.CapabilityUnit;
using CapabilityValue = LibHandheld.Contracts.CapabilityValue;
using CapabilityValueKind = LibHandheld.Contracts.CapabilityValueKind;
using CommandOutcome = LibHandheld.Contracts.CommandOutcome;
using CurvePoint = LibHandheld.Contracts.CurvePoint;

namespace LibHandheld;

public sealed partial class HandheldDevice
{
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Dictionary<(string Id, string? Instance), CapabilityState> _states = [];
    private CapabilityDescriptorSet? _descriptors;

    /// <summary>Current native controls and observations, without retaining mutable engine collections.</summary>
    public DeviceControlSnapshot Controls
    {
        get
        {
            lock (_operations)
            {
                return Snapshot();
            }
        }
    }

    /// <summary>
    ///     Sets one uniform watt target on the selected model's sustained and declared boost limits.
    ///     Native providers own the appropriate firmware order; no model identifiers or transport details are needed.
    /// </summary>
    public ValueTask<DeviceOperationResult> SetTdpAsync(int watts, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return ApplyControlsAsync(snapshot =>
        {
            var requests = new List<ControlRequest>
            {
                new(CapabilityRole.PowerSustainedLimit, CapabilityValue.Integer(watts))
            };
            foreach (var role in new[]
                     {
                         CapabilityRole.PowerSlowLimit, CapabilityRole.PowerFastLimit, CapabilityRole.PowerPeakLimit
                     })
            {
                if (snapshot.Controls.Any(control => control.Role == role && control.Descriptor.SupportsWrite))
                {
                    requests.Add(new ControlRequest(role, CapabilityValue.Integer(watts)));
                }
            }

            return requests;
        }, deadline, cancellationToken);
    }

    /// <summary>
    ///     Sets explicit sustained, slow, fast and peak targets where the model declares them. All bounds and pair
    ///     relationships are checked before the first write. Omitted limits are not inferred from another target.
    /// </summary>
    public ValueTask<DeviceOperationResult> SetPowerLimitsAsync(PowerLimits limits, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        return ApplyControlsAsync(_ =>
        {
            var requests = new List<ControlRequest>
            {
                new(CapabilityRole.PowerSustainedLimit, CapabilityValue.Integer(limits.SustainedWatts))
            };
            Add(CapabilityRole.PowerSlowLimit, limits.SlowWatts);
            Add(CapabilityRole.PowerFastLimit, limits.FastWatts);
            Add(CapabilityRole.PowerPeakLimit, limits.PeakWatts);
            return requests;

            void Add(CapabilityRole role, int? value)
            {
                if (value is { } watts)
                {
                    requests.Add(new ControlRequest(role, CapabilityValue.Integer(watts)));
                }
            }
        }, deadline, cancellationToken);
    }

    /// <summary>Sets a declared native performance mode. Available option names come from Controls.</summary>
    public ValueTask<DeviceOperationResult> SetScenarioModeAsync(string mode, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.ScenarioMode, CapabilityValue.Choice(mode), deadline: deadline,
            cancellationToken: cancellationToken);
    }

    /// <summary>Sets the declared fan mode for one logical fan, or the sole shared fan control.</summary>
    public ValueTask<DeviceOperationResult> SetFanModeAsync(string mode, string? fan = null, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.FanMode, CapabilityValue.Choice(mode), fan, deadline, cancellationToken);
    }

    /// <summary>Sets a fan duty percentage where that logical fan exposes direct duty control.</summary>
    public ValueTask<DeviceOperationResult> SetFanDutyAsync(int percent, string? fan = null, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.FanDuty, CapabilityValue.Integer(percent), fan, deadline,
            cancellationToken);
    }

    /// <summary>Sets a target RPM where the selected model exposes RPM control.</summary>
    public ValueTask<DeviceOperationResult> SetFanTargetRpmAsync(int rpm, string? fan = null, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.FanTargetRpm, CapabilityValue.Integer(rpm), fan, deadline,
            cancellationToken);
    }

    /// <summary>Sets a declared fan curve. Inputs are Celsius; outputs use the control's declared unit and bounds.</summary>
    public ValueTask<DeviceOperationResult> SetFanCurveAsync(IReadOnlyList<CurvePoint> points, string? fan = null,
        Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(points);
        return SetControlAsync(CapabilityRole.FanCurve, CapabilityValue.Curve(points), fan, deadline,
            cancellationToken);
    }

    /// <summary>Sets the battery charge ceiling using the selected model's real charging mechanism.</summary>
    public ValueTask<DeviceOperationResult> SetChargeLimitAsync(int percent, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.ChargeLimit, CapabilityValue.Integer(percent), deadline: deadline,
            cancellationToken: cancellationToken);
    }

    /// <summary>Enables or disables a declared Boolean battery-protection control.</summary>
    public ValueTask<DeviceOperationResult> SetChargeProtectionAsync(bool enabled, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.ChargeProtectionMode, CapabilityValue.Boolean(enabled),
            deadline: deadline,
            cancellationToken: cancellationToken);
    }

    /// <summary>Sets a declared choice battery-protection mode; choices are discoverable through Controls.</summary>
    public ValueTask<DeviceOperationResult> SetChargeProtectionModeAsync(string mode, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.ChargeProtectionMode, CapabilityValue.Choice(mode), deadline: deadline,
            cancellationToken: cancellationToken);
    }

    /// <summary>Sets a declared bypass-charging mode; choices are discoverable through Controls.</summary>
    public ValueTask<DeviceOperationResult> SetChargeBypassModeAsync(string mode, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.ChargeBypass, CapabilityValue.Choice(mode), deadline: deadline,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     Enables or disables real bypass charging using a Boolean control or declared always/disabled choices.
    ///     Other choice shapes are refused; use SetChargeBypassModeAsync for an explicit mode.
    ///     A charge ceiling is never substituted for bypass.
    /// </summary>
    public ValueTask<DeviceOperationResult> SetChargeBypassAsync(bool enabled, Deadline? deadline = null,
        CancellationToken cancellationToken = default)
    {
        return ApplyControlsAsync(snapshot =>
        {
            var descriptor = snapshot.Find(CapabilityRole.ChargeBypass)?.Descriptor;
            var value = descriptor is { ValueKind: CapabilityValueKind.Choice } &&
                        descriptor.Choices.Any(choice => choice.Value == "always") &&
                        descriptor.Choices.Any(choice => choice.Value == "disabled")
                ? CapabilityValue.Choice(enabled ? "always" : "disabled")
                : CapabilityValue.Boolean(enabled);
            return [new ControlRequest(CapabilityRole.ChargeBypass, value)];
        }, deadline, cancellationToken);
    }

    /// <summary>Enables or disables a declared lighting power control.</summary>
    public ValueTask<DeviceOperationResult> SetLightingPowerAsync(bool enabled, string? zone = null,
        Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.LightingPower, CapabilityValue.Boolean(enabled), zone, deadline,
            cancellationToken);
    }

    /// <summary>Sets declared lighting brightness. A logical zone is required when the model exposes several.</summary>
    public ValueTask<DeviceOperationResult> SetLightingBrightnessAsync(int percent, string? zone = null,
        Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.LightingBrightness, CapabilityValue.Integer(percent), zone, deadline,
            cancellationToken);
    }

    /// <summary>Sets the 24-bit RGB color of the selected logical lighting zone.</summary>
    public ValueTask<DeviceOperationResult> SetLightingColorAsync(int rgb, string? zone = null,
        Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.LightingZoneColor, CapabilityValue.Color(rgb), zone, deadline,
            cancellationToken);
    }

    /// <summary>Sets a declared lighting effect. The model's option names are exposed in Controls.</summary>
    public ValueTask<DeviceOperationResult> SetLightingEffectAsync(string effect, string? zone = null,
        Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.LightingEffect, CapabilityValue.Choice(effect), zone, deadline,
            cancellationToken);
    }

    /// <summary>Sets a declared lighting effect speed, using its published numeric bounds.</summary>
    public ValueTask<DeviceOperationResult> SetLightingSpeedAsync(int speed, string? zone = null,
        Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        return SetControlAsync(CapabilityRole.LightingEffectSpeed, CapabilityValue.Integer(speed), zone, deadline,
            cancellationToken);
    }

    /// <summary>
    ///     Sets one semantic role without exposing a capability identifier or a device-specific transport.
    ///     Ambiguous roles require a logical instance; unsupported operations are refused before dispatch.
    /// </summary>
    public ValueTask<DeviceOperationResult> SetControlAsync(CapabilityRole role, CapabilityValue value,
        string? instanceId = null, Deadline? deadline = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ApplyControlsAsync(_ => [new ControlRequest(role, value, instanceId)], deadline, cancellationToken);
    }

    private async ValueTask<DeviceOperationResult> ApplyControlsAsync(
        Func<DeviceControlSnapshot, IReadOnlyList<ControlRequest>> requests, Deadline? deadline,
        CancellationToken cancellationToken)
    {
        using var operation = Enter();
        var budget = deadline ?? Deadline.After(TimeSpan.FromSeconds(5));
        using var waiting = budget.CreateCancellationSource(cancellationToken);
        try
        {
            await _commands.WaitAsync(waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Refused(new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The request was cancelled or expired before native dispatch."));
        }

        try
        {
            var snapshot = Controls;
            var failure = Prepare(snapshot, requests(snapshot), budget, out var commands);
            if (failure is not null)
            {
                return Refused(failure);
            }

            var results = new List<DeviceControlResult>();
            foreach (var item in commands)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new DeviceOperationResult
                    {
                        Outcome = results.Count == 0 ? CommandOutcome.Rejected : CommandOutcome.Indeterminate,
                        Reason = new CapabilityReason(CapabilityReasonCode.Quiescing,
                            "The request was cancelled before its remaining commands were dispatched."),
                        Results = results
                    };
                }

                CapabilityCommandResult result;
                try
                {
                    result = await ExecuteCoreAsync(item.Command, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException))
                {
                    // Dispatch began. An exception does not prove the command stayed away from the hardware.
                    result = new CapabilityCommandResult
                    {
                        CommandId = item.Command.CommandId, Outcome = CommandOutcome.Indeterminate,
                        Reason = new CapabilityReason(CapabilityReasonCode.TransportFaulted, exception.Message),
                        CompletedAt = DateTimeOffset.UtcNow
                    };
                }

                results.Add(new DeviceControlResult(item.Role, item.Command.InstanceId, result));
                if (result.Outcome is not (CommandOutcome.AppliedVerified or CommandOutcome.AppliedUnverified))
                {
                    return new DeviceOperationResult
                    {
                        Outcome = result.Outcome, Reason = result.Reason, Results = results
                    };
                }
            }

            return new DeviceOperationResult
            {
                Outcome = results.All(result => result.Result.Outcome == CommandOutcome.AppliedVerified)
                    ? CommandOutcome.AppliedVerified
                    : CommandOutcome.AppliedUnverified,
                Results = results
            };
        }
        finally
        {
            _commands.Release();
        }
    }

    private static CapabilityReason? Prepare(DeviceControlSnapshot snapshot,
        IReadOnlyList<ControlRequest> requests, Deadline deadline, out List<PreparedCommand> commands)
    {
        commands = [];
        if (snapshot.DescriptorGeneration == 0)
        {
            return new CapabilityReason(CapabilityReasonCode.HostUnavailable, "Device controls have not started.");
        }

        if (deadline.HasExpired)
        {
            return new CapabilityReason(CapabilityReasonCode.Quiescing, "The requested deadline has passed.");
        }

        var selected = new List<(ControlRequest Request, DeviceControl Control)>();
        foreach (var request in requests)
        {
            var control = snapshot.Find(request.Role, request.InstanceId);
            if (control is null)
            {
                var exists = snapshot.Controls.Any(candidate => candidate.Role == request.Role);
                return new CapabilityReason(
                    exists ? CapabilityReasonCode.ValueOutOfRange : CapabilityReasonCode.Unsupported,
                    exists ? "Select one declared logical instance." : $"This model does not expose {request.Role}.");
            }

            if (!control.Descriptor.SupportsWrite)
            {
                return new CapabilityReason(CapabilityReasonCode.Unsupported, "The control is not writable.");
            }

            if (control.State is { Available: false } unavailable)
            {
                return unavailable.Reason ?? new CapabilityReason(CapabilityReasonCode.HostUnavailable);
            }

            if (!ValueMatches(control.Descriptor, request.Value))
            {
                return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange,
                    "The value does not match the declared shape, options or bounds.");
            }

            selected.Add((request, control));
        }

        if (selected.Any(item => IsPower(item.Request.Role) &&
                                 (item.Control.Descriptor.ValueKind != CapabilityValueKind.Integer ||
                                  item.Control.Descriptor.Unit != CapabilityUnit.Watt)))
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported,
                "A processor power control must declare integer watts.");
        }

        var power = selected.Where(item => IsPower(item.Request.Role)).ToArray();
        for (var index = 1; index < power.Length; index++)
        {
            if (power[index].Request.Value.IntegerValue < power[index - 1].Request.Value.IntegerValue)
            {
                return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange,
                    "Power targets must preserve sustained, slow, fast and peak order.");
            }
        }

        var paired = new HashSet<(string Id, string? Instance)>();
        foreach (var item in selected)
        {
            var descriptor = item.Control.Descriptor;
            int? pairedWatts = null;
            if (descriptor.PairedPowerLimitId is { } pairId)
            {
                var companions = selected.Where(candidate => candidate.Control.Descriptor.CapabilityId == pairId &&
                                                             candidate.Control.InstanceId is null).ToArray();
                if (companions.Length != 1 || !IsPower(companions[0].Request.Role) ||
                    descriptor.Role != CapabilityRole.PowerSustainedLimit || descriptor.InstanceId is not null)
                {
                    return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange,
                        "Both targets of the declared native power pair must be supplied.");
                }

                pairedWatts = companions[0].Request.Value.IntegerValue;
                if (item.Request.Value.IntegerValue > pairedWatts)
                {
                    return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange,
                        "Sustained power cannot exceed its paired boost target.");
                }

                paired.Add((pairId, null));
            }

            commands.Add(new PreparedCommand(item.Request.Role, new CapabilityCommand
            {
                CommandId = Guid.NewGuid(), CapabilityId = descriptor.CapabilityId,
                InstanceId = descriptor.InstanceId, RequestedValue = item.Request.Value,
                PairedPowerLimitWatts = pairedWatts, ExpectedCycleGeneration = snapshot.CycleGeneration,
                ExpectedDescriptorGeneration = snapshot.DescriptorGeneration, Deadline = deadline
            }));
        }

        // A paired native command writes both targets. Never dispatch its companion a second time.
        commands.RemoveAll(command => paired.Contains((command.Command.CapabilityId, command.Command.InstanceId)));
        // A direct semantic request for only the boost would otherwise omit its sustained companion.
        foreach (var item in selected.Where(item => IsPower(item.Request.Role)))
        {
            if (snapshot.Controls.Any(candidate => candidate.Descriptor.PairedPowerLimitId ==
                                                   item.Control.Descriptor.CapabilityId) && !paired.Contains((
                    item.Control.Descriptor.CapabilityId,
                    item.Control.InstanceId)))
            {
                return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange,
                    "Use SetPowerLimitsAsync to supply both targets of this native power pair.");
            }
        }

        return null;
    }

    private static bool IsPower(CapabilityRole role)
    {
        return role is CapabilityRole.PowerSustainedLimit or CapabilityRole.PowerSlowLimit or
            CapabilityRole.PowerFastLimit or CapabilityRole.PowerPeakLimit;
    }

    private static bool ValueMatches(CapabilityDescriptor descriptor, CapabilityValue value)
    {
        var native = new Internal.Capabilities.CapabilityDescriptor
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
            Role = (Internal.Capabilities.CapabilityRole)descriptor.Role,
            ValueKind = (Internal.Capabilities.CapabilityValueKind)descriptor.ValueKind,
            Display = new CapabilityDisplay
            {
                Key = DisplayKey.Custom, CustomLabel = descriptor.Label
            },
            Persistence = (CapabilityPersistence)descriptor.Persistence,
            Minimum = descriptor.Minimum, Maximum = descriptor.Maximum, Step = descriptor.Step,
            MaximumLength = descriptor.MaximumLength,
            Choices = descriptor.Choices.Select(choice =>
                new CapabilityChoice(choice.Value,
                    new CapabilityDisplay
                    {
                        Key = DisplayKey.Custom, CustomLabel = choice.Label
                    })).ToArray()
        };
        return CapabilityValueValidation.ValueMatches(HandheldConversions.ToNative(value),
            native, out _);
    }

    private DeviceControlSnapshot Snapshot()
    {
        return new DeviceControlSnapshot
        {
            CycleGeneration = _cycleGeneration, DescriptorGeneration = _descriptors?.Generation ?? 0,
            Controls = _descriptors?.Descriptors.Select(descriptor => new DeviceControl(descriptor,
                _states.GetValueOrDefault((descriptor.CapabilityId, descriptor.InstanceId)))).ToArray() ?? []
        };
    }

    private static DeviceOperationResult Refused(CapabilityReason reason)
    {
        return new DeviceOperationResult { Outcome = CommandOutcome.Rejected, Reason = reason };
    }

    private sealed record ControlRequest(CapabilityRole Role, CapabilityValue Value, string? InstanceId = null);

    private sealed record PreparedCommand(CapabilityRole Role, CapabilityCommand Command);
}
