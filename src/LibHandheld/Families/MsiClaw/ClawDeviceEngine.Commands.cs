using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Plugin;
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
                $"Capability '{CapabilityKey(command.CapabilityId, command.InstanceId)}' is not available.");
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
        var identity = cycleIdentity with { OnAcPower = _services.Identity.ReadOnAcPower() };
        var refusal = RefusalFor(
            service,
            FirmwareForCapability(command.CapabilityId),
            identity);
        refusal ??= ValidateCommand(command, descriptor, identity.OnAcPower);
        if (refusal is not null)
        {
            return CommandResults.Rejected(command, refusal);
        }

        CapabilityCommandResult result;
        try
        {
            result = await ApplyCapabilityCommandAsync(command, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceWriteBudgetException exception)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing, exception.Message, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Indeterminate(
                command,
                "Command was cancelled after hardware application began.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Indeterminate(
                command,
                $"Capability handler failed after admission: {ex.GetType().Name}.");
        }

        return result;
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

        return command.CapabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost => JournalCommandAsync(
                ServiceIds.Power,
                binding,
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) =>
                {
                    var (sustained, boost) = Limits();
                    return power.ApplyLimitsAsync(journalCommand, sustained, boost, token);
                },
                cancellationToken),
            CapabilityIds.Scenario => JournalCommandAsync(
                ServiceIds.Power,
                binding,
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplyScenarioAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.ChargeLimit => chargeLimit.ApplyAsync(
                command,
                command.RequestedValue!.IntegerValue!.Value,
                cancellationToken),
            CapabilityIds.FanMode => JournalCommandAsync(
                ServiceIds.Fans,
                binding,
                command,
                async token => ClawRecoveryValues.Fans(
                    await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => fans.ApplyModeAsync(
                    journalCommand,
                    journalCommand.RequestedValue!.ChoiceValue!,
                    token),
                cancellationToken),
            CapabilityIds.FanCurve => ApplyFanCurveCommandAsync(command, fans, binding, cancellationToken),
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

    private ValueTask<CapabilityCommandResult> ApplyFanCurveCommandAsync(
        CapabilityCommand command,
        ClawFanCapability fans,
        string? binding,
        CancellationToken cancellationToken)
    {
        return JournalCommandAsync(
            ServiceIds.Fans,
            binding,
            command,
            async token => ClawRecoveryValues.Fans(
                await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
            (journalCommand, token) => fans.ApplyCurveAsync(
                journalCommand,
                journalCommand.RequestedValue!.CurveValue,
                token),
            cancellationToken);
    }

    private DeviceServiceStatus? ServiceForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit => _chargeLimit,
            CapabilityIds.FanMode or CapabilityIds.FanCurve => _fans,
            CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    private static FirmwareKind FirmwareForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            // MCU-backed capabilities carry no revision gate. The controller mode switch is not an
            // addressed write, and lighting takes its address from HC's firmware table and, like HC,
            // writes without first confirming what the MCU holds.
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor
                or CapabilityIds.Controller or CapabilityIds.Rumble => FirmwareKind.None,
            // Read from the Windows sensor stack, not MSI firmware, so there is no firmware revision to
            // gate it on and gating it on the WMI one would refuse it whenever that path is degraded.
            CapabilityIds.Motion => FirmwareKind.None,
            _ => FirmwareKind.Wmi
        };
    }

    private static CapabilityReason? RefusalFor(
        DeviceServiceStatus service,
        FirmwareKind firmwareKind,
        ClawIdentityState identity)
    {
        if (!identity.ExactMachineMatch)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Exact device identity no longer matches the Claw implementation.",
                true);
        }

        if (!FirmwareVerified(identity, firmwareKind))
        {
            return new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "Current firmware is outside the Claw implementation's verified gate.");
        }

        return service.State switch
        {
            DeviceServiceState.Owned => null,
            DeviceServiceState.Passive => new CapabilityReason(
                CapabilityReasonCode.ResourceConflict,
                "The device service is passive or held by another owner.", true),
            DeviceServiceState.Releasing => new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "The device service is being released."),
            DeviceServiceState.Degraded => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is degraded and cannot accept commands."),
            DeviceServiceState.Faulted or DeviceServiceState.ReleasedUnverified => new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The device service is faulted or its release could not be verified."),
            _ => new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "The plugin does not currently own this device service.", true)
        };
    }

    private CapabilityReason? ValidateCommand(
        CapabilityCommand command,
        CapabilityDescriptor descriptor,
        bool onAcPower)
    {
        if (_descriptorSet is null
            || command.ExpectedDescriptorGeneration != _descriptorSet.Generation
            || command.ExpectedCycleGeneration != _cycleGeneration)
        {
            return new CapabilityReason(
                CapabilityReasonCode.GenerationChanged,
                "Command targets a descriptor or device generation that is no longer current.",
                true);
        }

        if (command.Deadline.HasExpired)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Command deadline passed before it could be applied.",
                true);
        }

        if (onAcPower ? !descriptor.AvailableOnAc : !descriptor.AvailableOnDc)
        {
            return new CapabilityReason(
                CapabilityReasonCode.UnavailableOnPowerSource,
                onAcPower
                    ? "Capability is unavailable on AC power."
                    : "Capability is unavailable on battery power.");
        }

        if (command.RequestedValue is null)
        {
            return descriptor.SupportsAction
                ? null
                : new CapabilityReason(
                    CapabilityReasonCode.Unsupported,
                    "Capability does not support being invoked as an action.");
        }

        if (!descriptor.SupportsWrite)
        {
            return new CapabilityReason(CapabilityReasonCode.Unsupported, "Capability is read-only.");
        }

        var value = command.RequestedValue;
        if (value.Kind != descriptor.ValueKind)
        {
            return new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                $"Value kind {value.Kind} does not match descriptor kind {descriptor.ValueKind}.");
        }

        if (!CapabilityValueValidation.ValueMatches(value, descriptor, out var invalid))
        {
            return ValueOutOfRange(invalid!);
        }

        // WSGM decides both limits of the pair; a command that does not carry a valid one is refused.
        return DevicePowerPair.Peer(_descriptorSet.Descriptors, command.CapabilityId) is not null
               && !DevicePowerPair.TryResolve(command, _descriptorSet.Descriptors, out _, out _, out var pairError)
            ? ValueOutOfRange(pairError!)
            : null;
    }

    private static CapabilityReason ValueOutOfRange(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.ValueOutOfRange, detail);
    }

    private static bool FirmwareVerified(ClawIdentityState identity, FirmwareKind kind)
    {
        return kind is not FirmwareKind.Wmi || identity.WmiAvailable;
    }

    private async ValueTask<CapabilityCommandResult> JournalCommandAsync(
        string serviceId,
        string? binding,
        CapabilityCommand command,
        Func<CancellationToken, ValueTask<ClawRecoveryState>> readOriginal,
        ClawCommandHandler apply,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted,
                "The recovery journal is unavailable; nothing was written.");
        }

        ClawRecoveryState? originalState = null;
        if (binding is null)
        {
            // HC writes without capturing anything. Without a BIOS version there is no firmware to bind
            // a restore point to, so the command is written the same way and there is nothing to restore.
            DeviceTrace.Change(serviceId, "journal",
                "The BIOS version is unknown, so the original has no binding; writing without a restore point.");
        }
        else
        {
            try
            {
                originalState = await readOriginal(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                return ClawApplied.Refused(command, "original-state capture", exception, cancellationToken);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                // When the original cannot be read, the command is written the same way and there is
                // nothing to restore; a read never gates a write.
                DeviceTrace.Failure(serviceId, "The original state could not be captured; writing without a restore",
                    ex);
            }
        }

        if (!DeviceWriteBudget.IsAvailable(command.Deadline))
        {
            return CommandResults.Rejected(command, new CapabilityReason(
                CapabilityReasonCode.Quiescing,
                "Not enough time left to write safely; nothing was written.",
                true));
        }

        if (binding is null || originalState is null)
        {
            return await apply(command, cancellationToken).ConfigureAwait(false);
        }

        // An entry left unverified or failed by an earlier restore is set pending again here: this
        // explicit command is the user action that lets the next release write its first original.
        DeviceRecoveryOperation<ClawRecoveryState> operation;
        try
        {
            operation = await _journal.BeginAsync(
                serviceId,
                binding,
                originalState,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Refused(command, "recovery journal", exception, cancellationToken);
        }

        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        var result = await apply(command, cancellationToken).ConfigureAwait(false);
        if (operation.Opened && result.Outcome is CommandOutcome.Rejected)
        {
            // Nothing was written, so the entry this command opened has nothing to restore.
            try
            {
                await _journal.SetStatusAsync(serviceId, DeviceRecoveryStatus.RestoredVerified, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Failure(serviceId, "The unused recovery entry could not be removed; release rewrites it",
                    ex);
            }
        }

        return result;
    }

    // Admission succeeded and the handler failed without confirming a rollback. Journalled resources
    // remain outstanding, so claiming any restoration here would be fabricated.
    private static CapabilityCommandResult Indeterminate(CapabilityCommand command, string detail)
    {
        return CommandResults.Indeterminate(
            command,
            CapabilityReasonCode.TransportFaulted,
            detail,
            RollbackResult.NotRequired);
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

    private enum FirmwareKind
    {
        None,
        Wmi
    }
}
