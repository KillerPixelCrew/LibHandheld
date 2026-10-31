using LibHandheld.Contracts;
using LibHandheld.Families.Asus;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

// Command validation and dispatch to the capabilities.
internal sealed partial class AllyDeviceEngine
{
    private async ValueTask<CapabilityCommandResult> ExecuteBoundCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        var descriptor = _descriptorSet?.Descriptors.FirstOrDefault(candidate =>
            candidate.CapabilityId == command.CapabilityId && candidate.InstanceId == command.InstanceId);
        var service = ServiceFor(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                $"Capability '{command.CapabilityId}' is not available.");
        }

        var identity = _cycleIdentity! with
        {
            OnAcPower = OnAcPower()
        };

        var refusal = Refusal(service, identity) ?? Validate(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return CommandResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A budget refusal is thrown before any write, so nothing needs restoring and the service stays
            // healthy. Anything else failed after hardware application began.
            return CommandResults.TransportFailure(command, dispatched: ex is not DeviceWriteBudgetException, ex);
        }

        if (result.Outcome is CommandOutcome.Applied
            && command.RequestedValue is { } requested)
        {
            // What was written stands in for a readback the firmware cannot give (SDK: an unverified
            // write earns Observed at best). It lives only for this cycle.
            var written = requested;
            if (command.CapabilityId == CapabilityIds.FanCurve
                && AsusFanCurve.TryEncode(requested.CurveValue, out var encoded, out _))
            {
                written = CapabilityValue.Curve(AsusFanCurve.Decode(encoded));
            }
            else if (command.CapabilityId == CapabilityIds.FanDuty && requested.IntegerValue is { } duty)
            {
                written = CapabilityValue.Integer(Math.Min(duty, 99));
            }

            _observedValues.Written(command.CapabilityId, written, command.InstanceId);
            if (command.CapabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
                && DevicePowerPair.TryResolve(command, _descriptorSet!.Descriptors, out var sustained, out var boost,
                    out _))
            {
                _observedValues.Written(CapabilityIds.PowerSustained, CapabilityValue.Integer(sustained));
                _observedValues.Written(CapabilityIds.PowerBoost, CapabilityValue.Integer(boost));
            }
            else if (command.CapabilityId == CapabilityIds.Scenario)
            {
                _observedValues.Forget(CapabilityIds.PowerSustained);
                _observedValues.Forget(CapabilityIds.PowerBoost);
                _observedValues.Forget(CapabilityIds.FanCurve);
                _observedValues.Forget(CapabilityIds.FanDuty);
                _observedValues.Forget(CapabilityIds.FanMode);
            }
            else if (command.CapabilityId == CapabilityIds.FanMode && requested.ChoiceValue == FanModes.Automatic)
            {
                _observedValues.Forget(CapabilityIds.FanDuty);
                _observedValues.Forget(CapabilityIds.FanCurve);
            }
        }

        return result;
    }

    private async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        AllyIdentityState identity,
        CancellationToken cancellationToken)
    {
        var value = command.RequestedValue!;
        Func<AllyIdentityState, CancellationToken, ValueTask<DeviceRecoveryOperation<AllyRecoveryState>?>>? prepare =
            command.CapabilityId switch
            {
                CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power!
                    .PrepareWriteAsync,
                CapabilityIds.FanCurve or CapabilityIds.FanDuty => _fans!.PrepareWriteAsync,
                CapabilityIds.FanMode when value.ChoiceValue == FanModes.Automatic => _fans!.PrepareWriteAsync,
                _ => null
            };
        DeviceRecoveryOperation<AllyRecoveryState>? operation = null;
        if (prepare is not null)
        {
            DeviceWriteBudget.Require(command.Deadline, "command preparation");
            try
            {
                operation = await prepare(identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return CommandResults.TransportFailure(command, dispatched: false, ex);
            }
        }

        // A thrown write keeps its original for recovery; a refusal wrote nothing and drops the one it opened.
        var result = await ApplyPreparedAsync(command, value, cancellationToken).ConfigureAwait(false);
        await _journal!.DiscardUnusedAsync(operation, result).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<CapabilityCommandResult> ApplyPreparedAsync(
        CapabilityCommand command,
        CapabilityValue value,
        CancellationToken cancellationToken)
    {
        switch (command.CapabilityId)
        {
            case CapabilityIds.PowerSustained or CapabilityIds.PowerBoost:
                if (!DevicePowerPair.TryResolve(command, _descriptorSet!.Descriptors, out var sustained, out var boost,
                        out _))
                {
                    throw new InvalidOperationException("A power limit command was admitted without a valid pair.");
                }


                return await _power!.Capability!.ApplyLimitsAsync(command, sustained, boost, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.Scenario:

                return await _power!.Capability!.ApplyScenarioAsync(command, value.ChoiceValue!, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.ChargeLimit:
                return _charge!.Capability!.Apply(command, value.IntegerValue!.Value);
            case CapabilityIds.FanDuty:
                return await _fans!.Capability!.ApplyDutyAsync(command, value.IntegerValue!.Value, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.FanCurve:

                return await _fans!.Capability!.ApplyCurveAsync(command, value.CurveValue, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.FanMode:
                return await ApplyFanModeAsync(command, value.ChoiceValue!, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingBrightness:
                return await _lighting!.ApplyAsync(command,
                        state => state with { Brightness = value.IntegerValue!.Value }, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingSpeed:
                return await _lighting!.ApplyAsync(command,
                    state => state with { Speed = value.IntegerValue!.Value }, cancellationToken).ConfigureAwait(false);
            case CapabilityIds.LightingEffect:
                return await _lighting!.ApplyAsync(command,
                        state => state with { Effect = Effects.Parse(value.ChoiceValue!) }, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.LightingColor:
                return await _lighting!.ApplyAsync(command, state => command.InstanceId == CapabilityInstances.Left
                    ? state with { LeftColor = value.ColorValue!.Value }
                    : state with { RightColor = value.ColorValue!.Value }, cancellationToken).ConfigureAwait(false);
            default:
                return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                    "This capability is read-only.");
        }
    }

    private async ValueTask<CapabilityCommandResult> ApplyFanModeAsync(
        CapabilityCommand command,
        string mode,
        CancellationToken cancellationToken)
    {
        var fans = _fans!;
        if (mode == FanModes.Automatic)
        {
            return await fans.Capability!.ApplyAutomaticAsync(command, fans.Original, cancellationToken)
                .ConfigureAwait(false);
        }

        // There is no custom-mode firmware switch. The next curve command changes the fans.
        return CommandResults.Applied(command);
    }

    private DeviceService<AllyIdentityState>? ServiceFor(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit => _charge,
            CapabilityIds.FanMode or CapabilityIds.FanCurve or CapabilityIds.FanReading or CapabilityIds.FanDuty =>
                _fans,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingEffect or CapabilityIds.LightingSpeed
                or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    private static CapabilityReason? Refusal(DeviceService<AllyIdentityState> service, AllyIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported,
                "The live identity no longer matches the Ally model.", true);
        }

        return DeviceServiceLifecycle.CommandRefusal(service);
    }

    private CapabilityReason? Validate(CapabilityCommand command, CapabilityDescriptor descriptor, bool onAcPower)
    {
        if (command.RequestedValue is null && descriptor.SupportsAction)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "Rumble is driven through haptic frames.");
        }

        // The descriptor was found in the published set, so the set exists.
        return CommandAdmission.Validate(command, descriptor, _descriptorSet!.Descriptors, onAcPower);
    }
}
