using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

// Command admission, dispatch to the capabilities and the journalled write.
internal sealed partial class ClawDeviceEngine
{
    private async ValueTask<CapabilityCommandResult> ExecuteBoundCommandAsync(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        var descriptor = _descriptorSet?.Descriptors.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, command.CapabilityId, StringComparison.Ordinal)
            && string.Equals(candidate.InstanceId, command.InstanceId, StringComparison.Ordinal));
        var service = ServiceForCapability(command.CapabilityId);
        if (descriptor is null || service is null)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.Unsupported,
                $"Capability '{command.CapabilityId}' is not available.");
        }

        if (_cycleIdentity is not { } cycleIdentity)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.ResourceReleased,
                "No device cycle is active.",
                true);
        }

        // SMBIOS, the model and the firmware binding cannot change within a cycle; start and resume
        // read them. Only the power source changes under a running cycle, so it is read per command.
        var identity = cycleIdentity with { OnAcPower = OnAcPower() };

        // No firmware revision gates a command: the definition matched at start, and MSI ships EC and MCU
        // updates that an allowlist would refuse.
        var refusal = DeviceServiceLifecycle.CommandRefusal(service)
                      ?? ValidateCommand(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return CommandResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyCapabilityCommandAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A budget refusal is thrown before its write. Anything else failed after admission without a
            // confirmed rollback, so journalled resources stay outstanding and the effect is unknown.
            return CommandResults.TransportFailure(command, dispatched: exception is not DeviceWriteBudgetException,
                exception);
        }

        if (result.Outcome is CommandOutcome.Applied && command.RequestedValue is { } requested)
        {
            RecordWritten(command, requested);
        }

        return result;
    }

    /// <summary>Records what an accepted command wrote as the observed value for the rest of the cycle.</summary>
    /// <remarks>
    ///     HC never reads a control back, so the written value stands in for a readback, with the knock-on
    ///     effects the firmware applies: a fixed duty is custom mode without the curve, a charge limit sets
    ///     Battery Master, and a lighting write carries the whole profile.
    /// </remarks>
    private void RecordWritten(CapabilityCommand command, CapabilityValue requested)
    {
        switch (command.CapabilityId)
        {
            case CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
                when DevicePowerPair.TryResolve(command, _descriptorSet!.Descriptors, out var sustained,
                    out var boost, out _):
                _observedValues.Written(CapabilityIds.PowerSustained, CapabilityValue.Integer(sustained));
                _observedValues.Written(CapabilityIds.PowerBoost, CapabilityValue.Integer(boost));
                return;
            case CapabilityIds.ChargeLimit:
                _observedValues.Written(CapabilityIds.ChargeLimit, requested);
                _observedValues.Written(CapabilityIds.ChargeProtection, CapabilityValue.Boolean(true));
                return;
            case CapabilityIds.ChargeProtection:
                _observedValues.Written(CapabilityIds.ChargeProtection, requested);
                if (_chargeLimitCapability?.WrittenPercent is { } percent)
                {
                    _observedValues.Written(CapabilityIds.ChargeLimit, CapabilityValue.Integer(percent));
                }

                return;
            case CapabilityIds.FanDuty:
                _observedValues.Written(CapabilityIds.FanDuty, requested);
                _observedValues.Written(CapabilityIds.FanMode, CapabilityValue.Choice("custom"));
                _observedValues.Forget(CapabilityIds.FanCurve);
                return;
            case CapabilityIds.LightingBrightness or CapabilityIds.LightingColor
                when _lightingCapability?.Current is { } lighting:
                _observedValues.Written(CapabilityIds.LightingBrightness,
                    CapabilityValue.Integer(lighting.Brightness));
                _observedValues.Written(CapabilityIds.LightingColor, CapabilityValue.Color(lighting.LeftRingColor),
                    CapabilityInstances.LeftRing);
                _observedValues.Written(CapabilityIds.LightingColor, CapabilityValue.Color(lighting.RightRingColor),
                    CapabilityInstances.RightRing);
                _observedValues.Written(CapabilityIds.LightingColor, CapabilityValue.Color(lighting.ButtonsColor),
                    CapabilityInstances.Buttons);
                return;
            default:
                _observedValues.Written(command.CapabilityId, requested, command.InstanceId);
                return;
        }
    }

    private ValueTask<CapabilityCommandResult> ApplyCapabilityCommandAsync(
        CapabilityCommand command,
        ClawIdentityState identity,
        CancellationToken cancellationToken)
    {
        // Power and fans bind their restore point to the BIOS version; without one they write without it.
        var binding = identity.RecoveryBinding;

        (int Sustained, int Boost) Limits()
        {
            return DevicePowerPair.TryResolve(command, _descriptorSet!.Descriptors, out var sustained, out var boost,
                out _)
                ? (sustained, boost)
                : throw new InvalidOperationException("A power limit command was admitted without a valid pair.");
        }

        var power = _powerCapability
                    ?? throw new InvalidOperationException("The power capability is unavailable.");
        var chargeLimit = _chargeLimitCapability
                          ?? throw new InvalidOperationException("The charge-limit capability is unavailable.");
        var fans = _fanCapability
                   ?? throw new InvalidOperationException("The fan capability is unavailable.");
        var lighting = _lightingCapability
                       ?? throw new InvalidOperationException("The lighting capability is unavailable.");

        // Every power command journals the same original first, and so does every fan command.
        ValueTask<CapabilityCommandResult> PowerCommand(ClawCommandHandler apply)
        {
            return JournalCommandAsync(
                ClawServiceIds.Power,
                binding,
                command,
                async token => ClawRecoveryState.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                apply,
                cancellationToken);
        }

        ValueTask<CapabilityCommandResult> FanCommand(ClawCommandHandler apply)
        {
            return JournalCommandAsync(
                ClawServiceIds.Fans,
                binding,
                command,
                async token => ClawRecoveryState.Fans(
                    await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                apply,
                cancellationToken);
        }

        if (command.CapabilityId == CapabilityIds.PowerBackend)
        {
            return SelectPowerPathAsync(command, identity, power, cancellationToken);
        }

        if (command.CapabilityId is CapabilityIds.PowerSustained or CapabilityIds.PowerBoost
            && _direct is { Direct: true } direct)
        {
            var (sustained, boost) = Limits();
            return WriteDirectLimitsAsync(command, identity, power, direct, sustained, boost, cancellationToken);
        }

        return command.CapabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost => PowerCommand((journalCommand, token) =>
            {
                var (sustained, boost) = Limits();
                return power.ApplyLimitsAsync(journalCommand, sustained, boost, token);
            }),
            CapabilityIds.Scenario => PowerCommand((journalCommand, token) => power.ApplyScenarioAsync(
                journalCommand,
                journalCommand.RequestedValue!.ChoiceValue!,
                token)),
            CapabilityIds.ChargeLimit => chargeLimit.ApplyAsync(
                command,
                command.RequestedValue!.IntegerValue!.Value,
                cancellationToken),
            CapabilityIds.ChargeProtection => chargeLimit.ApplyProtectionAsync(
                command, command.RequestedValue!.BooleanValue!.Value, cancellationToken),
            CapabilityIds.FanMode => FanCommand((journalCommand, token) => fans.ApplyModeAsync(
                journalCommand,
                journalCommand.RequestedValue!.ChoiceValue!,
                token)),
            CapabilityIds.FanDuty => FanCommand((journalCommand, token) => fans.ApplyDutyAsync(
                journalCommand,
                journalCommand.RequestedValue!.IntegerValue!.Value,
                token)),
            CapabilityIds.FanCurve => FanCommand((journalCommand, token) => fans.ApplyCurveAsync(
                journalCommand,
                journalCommand.RequestedValue!.CurveValue,
                token)),
            CapabilityIds.LightingBrightness => lighting.ApplyAsync(
                command,
                state => state with
                {
                    Brightness = command.RequestedValue!.IntegerValue!.Value
                },
                cancellationToken),
            CapabilityIds.LightingColor => lighting.ApplyAsync(
                command,
                state => command.InstanceId switch
                {
                    CapabilityInstances.RightRing => state with
                    {
                        RightRingColor = command.RequestedValue!.ColorValue!.Value
                    },
                    CapabilityInstances.LeftRing => state with
                    {
                        LeftRingColor = command.RequestedValue!.ColorValue!.Value
                    },
                    CapabilityInstances.Buttons => state with
                    {
                        ButtonsColor = command.RequestedValue!.ColorValue!.Value
                    },
                    _ => state
                },
                cancellationToken),
            _ => ReadOnlyHandler(command, cancellationToken)
        };
    }

    /// <summary>Opens the WMI pair to the model maximum once, then writes the limits to the CPU registers.</summary>
    private async ValueTask<CapabilityCommandResult> WriteDirectLimitsAsync(CapabilityCommand command,
        ClawIdentityState identity, ClawPowerCapability power, ClawDirectPowerService direct, int sustained, int boost,
        CancellationToken cancellationToken)
    {
        if (!direct.FirmwareOpened)
        {
            var opened = await PowerLimitsAsync(command, identity, power, Model.MaximumWatts, Model.MaximumWatts,
                cancellationToken).ConfigureAwait(false);
            if (opened.Outcome != CommandOutcome.Applied)
            {
                return opened;
            }

            direct.FirmwareOpened = true;
            DeviceTrace.Change(ClawServiceIds.Power, "power-path",
                $"WMI opened to {Model.MaximumWatts}/{Model.MaximumWatts} W; the CPU registers carry the limits.");
        }

        var dispatched = false;
        try
        {
            await direct.WriteAsync(sustained, boost, ClawDirectPowerService.Binding(identity), () => dispatched = true,
                cancellationToken).ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Writes a WMI pair, journalling the power original first like every power command.</summary>
    private ValueTask<CapabilityCommandResult> PowerLimitsAsync(CapabilityCommand command, ClawIdentityState identity,
        ClawPowerCapability power, int sustained, int boost, CancellationToken cancellationToken)
    {
        return JournalCommandAsync(ClawServiceIds.Power, identity.RecoveryBinding, command,
            async token => ClawRecoveryState.Power(await power.ReadAsync(token).ConfigureAwait(false)),
            (journalCommand, token) => power.ApplyLimitsAsync(journalCommand, sustained, boost, token),
            cancellationToken);
    }

    /// <summary>Switches between WMI, PawnIO and KX and re-applies the published pair through the new path.</summary>
    /// <remarks>A pair that could not be re-applied is no longer known, so it is published as unknown.</remarks>
    private async ValueTask<CapabilityCommandResult> SelectPowerPathAsync(CapabilityCommand command,
        ClawIdentityState identity, ClawPowerCapability power, CancellationToken cancellationToken)
    {
        if (_direct is not { } direct || !direct.Paths.TryParse(command, out var path))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "This power-limit path is not available on this device.");
        }

        if (path == direct.Paths.Selected)
        {
            return CommandResults.Applied(command);
        }

        var sustained = _observedValues.Value(CapabilityIds.PowerSustained)?.IntegerValue;
        var boost = _observedValues.Value(CapabilityIds.PowerBoost)?.IntegerValue;
        var binding = ClawDirectPowerService.Binding(identity);
        try
        {
            await direct.Paths.SelectAsync(path, binding, cancellationToken).ConfigureAwait(false);
            if (path == IntelPowerPath.Firmware)
            {
                await direct.RestoreMsrAsync(binding, cancellationToken).ConfigureAwait(false);
                direct.FirmwareOpened = false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, true, exception);
        }

        if (sustained is { } pl1 && boost is { } pl2)
        {
            var reapply = command with
            {
                CapabilityId = CapabilityIds.PowerSustained, RequestedValue = CapabilityValue.Integer(pl1),
                PairedPowerLimitWatts = pl2
            };
            var result = direct.Direct
                ? await WriteDirectLimitsAsync(reapply, identity, power, direct, pl1, pl2, cancellationToken)
                    .ConfigureAwait(false)
                : await PowerLimitsAsync(reapply, identity, power, pl1, pl2, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == CommandOutcome.Applied)
            {
                _observedValues.Written(CapabilityIds.PowerSustained, CapabilityValue.Integer(pl1));
                _observedValues.Written(CapabilityIds.PowerBoost, CapabilityValue.Integer(pl2));
            }
            else
            {
                _observedValues.Forget(CapabilityIds.PowerSustained);
                _observedValues.Forget(CapabilityIds.PowerBoost);
                DeviceTrace.Warn(ClawServiceIds.Power,
                    $"Limits were not re-applied after the path change: {result.Reason?.Detail}");
            }
        }

        return CommandResults.Applied(command);
    }

    private DeviceServiceStatus? ServiceForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.PowerBackend => _direct,
            CapabilityIds.ChargeLimit or CapabilityIds.ChargeProtection => _chargeLimit,
            CapabilityIds.FanMode or CapabilityIds.FanCurve or CapabilityIds.FanDuty => _fans,
            CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    private CapabilityReason? ValidateCommand(
        CapabilityCommand command,
        CapabilityDescriptor descriptor,
        bool onAcPower)
    {
        return _descriptorSet is null
            ? new CapabilityReason(CapabilityReasonCode.HostUnavailable)
            : CommandAdmission.Validate(command, descriptor, _descriptorSet.Descriptors, onAcPower);
    }

    private async ValueTask<CapabilityCommandResult> JournalCommandAsync(
        string serviceId,
        string? binding,
        CapabilityCommand command,
        Func<CancellationToken, ValueTask<ClawRecoveryState>> readOriginal,
        ClawCommandHandler apply,
        CancellationToken cancellationToken)
    {
        DeviceRecoveryOperation<ClawRecoveryState>? operation = null;
        if (binding is not null && _journal is not null)
        {
            try
            {
                operation = await _journal.BeginAsync<ClawRecoveryState>(serviceId, binding,
                    async ct => await readOriginal(ct).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                return CommandResults.TransportFailure(command, dispatched: false, exception);
            }
        }

        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        var result = !DeviceWriteBudget.IsAvailable(command.Deadline) || cancellationToken.IsCancellationRequested
            ? CommandResults.Rejected(command, CapabilityReasonCode.Quiescing, "Cancelled before native dispatch.",
                true)
            : await apply(command, cancellationToken).ConfigureAwait(false);
        if (_journal is not null)
        {
            await _journal.DiscardUnusedAsync(operation, result).ConfigureAwait(false);
        }

        return result;
    }

    private static ValueTask<CapabilityCommandResult> ReadOnlyHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommandResults.Rejected(
            command,
            CapabilityReasonCode.Unsupported,
            "This capability is read-only."));
    }

    private delegate ValueTask<CapabilityCommandResult> ClawCommandHandler(
        CapabilityCommand command,
        CancellationToken cancellationToken);
}
