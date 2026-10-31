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
            ClawChargeLimitCapability.ProtectionId => chargeLimit.ApplyProtectionAsync(
                command, command.RequestedValue!.BooleanValue!.Value, cancellationToken),
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
            "fan.duty" => JournalCommandAsync(ServiceIds.Fans, binding, command,
                async token => ClawRecoveryValues.Fans(await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                (request, token) => fans.ApplyDutyAsync(request, request.RequestedValue!.IntegerValue!.Value, token),
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

    /// <summary>Opens the WMI pair to the model maximum once, then writes the limits to the CPU registers.</summary>
    private async ValueTask<CapabilityCommandResult> WriteDirectLimitsAsync(CapabilityCommand command,
        ClawIdentityState identity, ClawPowerCapability power, ClawDirectPowerService direct, int sustained, int boost,
        CancellationToken cancellationToken)
    {
        if (!direct.FirmwareOpened)
        {
            var opened = await JournalCommandAsync(ServiceIds.Power, identity.RecoveryBinding, command,
                async token => ClawRecoveryValues.Power(await power.ReadAsync(token).ConfigureAwait(false)),
                (journalCommand, token) => power.ApplyLimitsAsync(journalCommand, Model.MaximumWatts,
                    Model.MaximumWatts, token),
                cancellationToken).ConfigureAwait(false);
            if (opened.Outcome != CommandOutcome.Applied)
            {
                return opened;
            }

            direct.FirmwareOpened = true;
            DeviceTrace.Change("power", "power-path",
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

    /// <summary>Switches between WMI, PawnIO and KX and re-applies the current pair through the new path.</summary>
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

        var last = direct.Direct ? direct.Written : power.WrittenLimits;
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

        if (last is { } pair)
        {
            var reapply = command with
            {
                CapabilityId = CapabilityIds.PowerSustained, RequestedValue = CapabilityValue.Integer(pair.Sustained),
                PairedPowerLimitWatts = pair.Boost
            };
            var result = direct.Direct
                ? await WriteDirectLimitsAsync(reapply, identity, power, direct, pair.Sustained, pair.Boost,
                    cancellationToken).ConfigureAwait(false)
                : await JournalCommandAsync(ServiceIds.Power, identity.RecoveryBinding, reapply,
                    async token => ClawRecoveryValues.Power(await power.ReadAsync(token).ConfigureAwait(false)),
                    (journalCommand, token) => power.ApplyLimitsAsync(journalCommand, pair.Sustained, pair.Boost,
                        token),
                    cancellationToken).ConfigureAwait(false);
            if (result.Outcome != CommandOutcome.Applied)
            {
                DeviceTrace.Warn("power", $"Limits were not re-applied after the path change: {result.Reason?.Detail}");
            }
        }

        return CommandResults.Applied(command);
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
            CapabilityIds.PowerBackend => _direct,
            CapabilityIds.ChargeLimit or ClawChargeLimitCapability.ProtectionId => _chargeLimit,
            CapabilityIds.FanMode or CapabilityIds.FanCurve or "fan.duty" => _fans,
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
            // The path switch itself is no WMI write; a re-applied pair goes through its own path.
            CapabilityIds.PowerBackend => FirmwareKind.None,
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
                CapabilityReasonCode.Unsupported,
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
        return _descriptorSet is null
            ? new CapabilityReason(CapabilityReasonCode.HostUnavailable)
            : CommandAdmission.Validate(command, descriptor, _descriptorSet.Descriptors, onAcPower);
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
                return ClawApplied.Refused(command, "original-state capture", exception, cancellationToken);
            }
        }

        if (!DeviceWriteBudget.IsAvailable(command.Deadline) || cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "Cancelled before native dispatch.");
        }

        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        var result = await apply(command, cancellationToken).ConfigureAwait(false);
        if (operation?.Opened == true && result.Outcome is CommandOutcome.Rejected)
        {
            // Nothing was written, so the entry this command opened has nothing to restore.
            try
            {
                await _journal!.SetStatusAsync(serviceId, DeviceRecoveryStatus.RestoredVerified, CancellationToken.None)
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
            detail);
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
