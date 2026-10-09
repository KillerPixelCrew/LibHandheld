using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

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
            OnAcPower = _hardware.OnAcPower?.Invoke() ?? WindowsMachineIdentity.OnAcPower()
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
        catch (DeviceWriteBudgetException ex)
        {
            // Thrown before any write, so nothing needs restoring and the service stays healthy.
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing, ex.Message, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Indeterminate(command, CapabilityReasonCode.Quiescing,
                "The command was cancelled after hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException("The capability handler failed after admission", ex));
        }

        if (result.Outcome is CommandOutcome.Applied
            && command.RequestedValue is { } requested)
        {
            // What was written stands in for a readback the firmware cannot give (SDK: an unverified
            // write earns Observed at best). It lives only for this cycle.
            _observedValues.Written(command.CapabilityId, requested, command.InstanceId);
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
                _observedValues.Forget("fan.duty");
                _observedValues.Forget(CapabilityIds.FanMode);
            }
            else if (command.CapabilityId == CapabilityIds.FanMode && requested.ChoiceValue == FanModes.Automatic)
            {
                _observedValues.Forget("fan.duty");
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
        Func<AllyIdentityState, CancellationToken, ValueTask>? prepare = command.CapabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power!
                .PrepareWriteAsync,
            CapabilityIds.FanCurve or "fan.duty" => _fans!.PrepareWriteAsync,
            CapabilityIds.FanMode when value.ChoiceValue == FanModes.Automatic => _fans!.PrepareWriteAsync,
            _ => null
        };
        if (prepare is not null)
        {
            DeviceWriteBudget.Require(command.Deadline, "command preparation");
            try
            {
                await prepare(identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return CommandResults.Rejected(command,
                    ex is OperationCanceledException && cancellationToken.IsCancellationRequested
                        ? CapabilityReasonCode.Quiescing
                        : CapabilityReasonCode.TransportFaulted,
                    DiagnosticText.FromException("Preparation failed before any write", ex));
            }
        }

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
            case "fan.duty":
                return await _fans!.Capability!.ApplyDutyAsync(command, value.IntegerValue!.Value, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.FanCurve:

                return await _fans!.Capability!.ApplyCurveAsync(command, value.CurveValue, cancellationToken)
                    .ConfigureAwait(false);
            case CapabilityIds.FanMode:
                return await ApplyFanModeAsync(command, identity, value.ChoiceValue!, cancellationToken)
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
        AllyIdentityState identity,
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
            CapabilityIds.FanMode or CapabilityIds.FanCurve or CapabilityIds.FanReading or "fan.duty" => _fans,
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

        return service.State switch
        {
            DeviceServiceState.Owned => null,
            DeviceServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing,
                service.Reason?.Detail ?? "The device service is unavailable."),
            DeviceServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            DeviceServiceState.Degraded or DeviceServiceState.Faulted or DeviceServiceState.ReleasedUnverified =>
                new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    service.Reason?.Detail ?? "The device service is faulted."),
            _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", true)
        };
    }

    private CapabilityReason? Validate(CapabilityCommand command, CapabilityDescriptor descriptor, bool onAcPower)
    {
        if (command.RequestedValue is null && descriptor.SupportsAction)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "Rumble is driven through haptic frames.");
        }

        return _descriptorSet is null
            ? new CapabilityReason(CapabilityReasonCode.HostUnavailable)
            : CommandAdmission.Validate(command, descriptor, _descriptorSet.Descriptors, onAcPower);
    }

    private static CapabilityReason OutOfRange(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail);
    }
}
