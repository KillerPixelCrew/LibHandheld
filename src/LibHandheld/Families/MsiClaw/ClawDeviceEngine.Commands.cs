using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
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
        var refusal = RefusalFor(service) ?? ValidateCommand(command, descriptor, identity.OnAcPower);
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

        // Every power command journals the same original first, and so does every fan command.
        ValueTask<CapabilityCommandResult> PowerCommand(ClawCommandHandler apply)
        {
            return JournalCommandAsync(
                ServiceIds.Power,
                binding,
                command,
                async token => ClawRecoveryValues.Power(
                    await power.ReadAsync(token).ConfigureAwait(false)),
                apply,
                cancellationToken);
        }

        ValueTask<CapabilityCommandResult> FanCommand(ClawCommandHandler apply)
        {
            return JournalCommandAsync(
                ServiceIds.Fans,
                binding,
                command,
                async token => ClawRecoveryValues.Fans(
                    await fans.ReadSnapshotAsync(token).ConfigureAwait(false)),
                apply,
                cancellationToken);
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

    private DeviceServiceStatus? ServiceForCapability(string capabilityId)
    {
        return capabilityId switch
        {
            CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario => _power,
            CapabilityIds.ChargeLimit or CapabilityIds.ChargeProtection => _chargeLimit,
            CapabilityIds.FanMode or CapabilityIds.FanCurve or CapabilityIds.FanDuty => _fans,
            CapabilityIds.FanRpm or CapabilityIds.Temperature => _telemetry,
            CapabilityIds.LightingBrightness or CapabilityIds.LightingColor => _lighting,
            CapabilityIds.Controller or CapabilityIds.Rumble => _controller,
            CapabilityIds.Motion => _motion,
            _ => null
        };
    }

    /// <summary>Why the owning service cannot take a command now, or null when it owns its hardware.</summary>
    /// <remarks>
    ///     No firmware revision gates a command: the definition matched at start, and MSI ships EC and MCU
    ///     updates that an allowlist would refuse.
    /// </remarks>
    private static CapabilityReason? RefusalFor(DeviceServiceStatus service)
    {
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

        if (!DeviceWriteBudget.IsAvailable(command.Deadline) || cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "Cancelled before native dispatch.", true);
        }

        // A transport exception does not prove whether the firmware accepted a write. Let it
        // propagate while the exact pre-command journal entry remains outstanding for recovery.
        var result = await apply(command, cancellationToken).ConfigureAwait(false);
        if (operation?.Opened == true && result.Outcome is CommandOutcome.Rejected)
        {
            // Nothing was written, so the entry this command opened has nothing to restore.
            try
            {
                await _journal!.MarkRestoredAsync(serviceId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Failure(serviceId, "The unused recovery entry could not be removed; release rewrites it",
                    ex);
            }
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
