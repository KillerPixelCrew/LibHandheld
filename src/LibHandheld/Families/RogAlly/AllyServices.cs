using System.ComponentModel;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

internal static class AllyServiceIds
{
    public const string VendorEvents = "asus-vendor-events";
    public const string Keyboard = "asus-oem-keyboard";
    public const string Power = "asus-power";
    public const string Fans = "asus-fans";
    public const string ChargeLimit = "asus-charge-limit";
    public const string Lighting = "asus-aura";
    public const string Motion = "ally-motion";
    public const string Controller = "physical-controller";

    /// <summary>Journal identity for the controller tables, which do not depend on BIOS.</summary>
    public const string McuFirmware = "mcu";
}

/// <summary>Power limits and performance mode.</summary>
/// <param name="acpi">Shared ATKACPI transport for power policy.</param>
/// <param name="delay">Cancellable spacing for mode/limit writes.</param>
/// <param name="journal">Durable original-state store bound to the current firmware.</param>
internal sealed class PowerService(
    IAsusAcpi acpi,
    Func<TimeSpan, CancellationToken, Task> delay,
    DeviceRecoveryJournal journal) : DeviceService<AllyIdentityState>(AllyServiceIds.Power)
{
    public AllyPowerCapability? Capability { get; private set; }

    public AllyPowerState? LastObserved { get; private set; }

    public override ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();


        if (context.Identity.Model is not { } model)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The exact Ally identity no longer matches.")));
        }

        if (!acpi.TryOpen())
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The ASUS System Control Interface driver (ATKACPI) is not installed.")));
        }

        Capability = new AllyPowerCapability(acpi, model, delay);
        LastObserved = Capability.Read();
        DeviceTrace.Info("power",
            $"SPL={LastObserved.Sustained?.ToString() ?? "?"} SPPT={LastObserved.Slow?.ToString() ?? "?"} "
            + $"FPPT={LastObserved.Fast?.ToString() ?? "?"} mode={LastObserved.Mode?.ToString() ?? "?"}.");
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    public void Refresh()
    {
        LastObserved = Capability?.Read();
    }

    /// <summary>Journals the original state before the first write of this cycle, when it can be read.</summary>
    /// <remarks>
    ///     A firmware that cannot report its limits and mode is written anyway, as HC does
    ///     (<c>ROGAlly.cs:694-702</c>). Nothing is journalled then, so stop has nothing to restore.
    /// </remarks>
    /// <param name="identity">Current verified machine and firmware binding for the original state.</param>
    /// <param name="cancellationToken">Cancels journal persistence; no hardware mutation is performed here.</param>
    /// <returns>
    ///     Completion after preserving the first readable original, or without a new entry when already held or
    ///     unreadable.
    /// </returns>
    public async ValueTask PrepareWriteAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        await journal.BeginAsync(ServiceId, identity.FirmwareIdentity, () =>
        {
            var original = Capability!.Read();
            return original.LimitsReadable && original.Mode is not null ? AllyRecoveryState.Power(original) : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        // A faulted service may still hold captured state; stop is its restore point.
        if (State is not (DeviceServiceState.Owned or DeviceServiceState.Faulted) || Capability is null)
        {
            return Set(DeviceServiceState.Idle);
        }

        if (journal.PendingOriginalFor<AllyRecoveryState>(ServiceId)?.ToPower() is { } original)
        {
            if (!DeviceWriteBudget.IsAvailable(context.Deadline))
            {
                return Set(DeviceServiceState.ReleasedUnverified, NoTimeToRestore());
            }

            try
            {
                await Capability.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
                await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredVerified, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Failure("power", "Power restoration failed; the recovery entry stays pending", ex);
                return Set(DeviceServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                    DiagnosticText.FromException("Power restoration failed", ex)));
            }
        }

        return Set(DeviceServiceState.Idle);
    }

    /// <summary>A release refused for lack of time: nothing was written and the entry stays pending.</summary>
    /// <returns>A Quiescing reason indicating that restore was not dispatched and its original remains pending.</returns>
    internal static CapabilityReason NoTimeToRestore()
    {
        return new CapabilityReason(CapabilityReasonCode.Quiescing,
            "Not enough time to restore; the original stays recorded for the next start.");
    }
}

/// <summary>Fan curves and fan readings.</summary>
/// <param name="acpi">Shared ATKACPI transport for fan probes and curve writes.</param>
/// <param name="delay">Cancellable spacing between channel writes.</param>
/// <param name="journal">Durable original fan curves bound to the current firmware.</param>
internal sealed class FanService(
    IAsusAcpi acpi,
    Func<TimeSpan, CancellationToken, Task> delay,
    DeviceRecoveryJournal journal) : DeviceService<AllyIdentityState>(AllyServiceIds.Fans)
{
    public AllyFanCapability? Capability { get; private set; }

    public AllyFanSnapshot? LastObserved { get; private set; }

    public (int? Cpu, int? Gpu) LastFans { get; private set; }

    public AllyFanSnapshot? Original => journal.OriginalStateFor<AllyRecoveryState>(ServiceId)?.ToFans();

    public override ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();


        if (!context.Identity.ExactMachineMatch)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The exact Ally identity no longer matches.")));
        }

        if (!acpi.TryOpen())
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The ASUS System Control Interface driver (ATKACPI) is not installed.")));
        }

        Capability = new AllyFanCapability(acpi, delay);
        Capability.Probe();
        Refresh();
        DeviceTrace.Info("fans",
            $"curves readable={LastObserved?.Readable == true}, mid fan={Capability.HasMidFan}.");
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    public void Refresh()
    {
        if (Capability is null)
        {
            return;
        }

        LastObserved = Capability.Read();
        LastFans = Capability.ReadFans();
    }

    /// <summary>Journals the original curves before the first write of this cycle, when they can be read.</summary>
    /// <remarks>
    ///     A firmware that refuses the curve query is written anyway, as HC does; stop then returns the
    ///     fans to HC's factory tables rather than to a captured original (<c>ROGAlly.cs:466-478</c>).
    /// </remarks>
    /// <param name="identity">Current verified machine and firmware binding for the original state.</param>
    /// <param name="cancellationToken">Cancels journal persistence; no hardware mutation is performed here.</param>
    /// <returns>
    ///     Completion after preserving the first readable original, or without a new entry when already held or
    ///     unreadable.
    /// </returns>
    public async ValueTask PrepareWriteAsync(AllyIdentityState identity, CancellationToken cancellationToken)
    {
        await journal.BeginAsync(ServiceId, identity.FirmwareIdentity, () =>
        {
            var original = Capability!.Read();
            return original.Readable && original.Mode is not null && (!Capability.HasMidFan || original.Mid is not null)
                ? AllyRecoveryState.Fans(original)
                : null;
        }, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (State is not (DeviceServiceState.Owned or DeviceServiceState.Faulted) || Capability is null)
        {
            return Set(DeviceServiceState.Idle);
        }

        if (journal.PendingOriginalFor<AllyRecoveryState>(ServiceId)?.ToFans() is not { } original)
        {
            return await ReleaseToFactoryAsync(context, cancellationToken).ConfigureAwait(false);
        }

        if (!DeviceWriteBudget.IsAvailable(context.Deadline))
        {
            return Set(DeviceServiceState.ReleasedUnverified, PowerService.NoTimeToRestore());
        }

        try
        {
            await Capability.RestoreAsync(original, cancellationToken).ConfigureAwait(false);
            await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredVerified, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("fans", "Fan restoration failed; the recovery entry stays pending", ex);
            return Set(DeviceServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException("Fan restoration failed", ex)));
        }

        return Set(DeviceServiceState.Idle);
    }

    /// <summary>Without a captured original, a custom curve is replaced by HC's factory tables.</summary>
    private async ValueTask<DeviceServiceResult> ReleaseToFactoryAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (Capability?.HasCustomWrite != true)
        {
            return Set(DeviceServiceState.Idle);
        }

        if (!DeviceWriteBudget.IsAvailable(context.Deadline))
        {
            return Set(DeviceServiceState.ReleasedUnverified, new CapabilityReason(CapabilityReasonCode.Quiescing,
                "Not enough time to write the factory fan tables back."));
        }

        try
        {
            await Capability.WriteFactoryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("fans", "Writing the factory fan tables failed", ex);
            return Set(DeviceServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException("Fan restoration failed", ex)));
        }

        return Set(DeviceServiceState.Idle);
    }
}

/// <summary>Battery charge ceiling: a persistent user choice, never reverted on stop.</summary>
/// <param name="acpi">Shared ATKACPI transport for the persistent user-selected charge ceiling.</param>
internal sealed class ChargeLimitService(IAsusAcpi acpi) : DeviceService<AllyIdentityState>(AllyServiceIds.ChargeLimit)
{
    public AllyChargeLimitCapability? Capability { get; private set; }

    public int? LastObserved { get; private set; }

    public override ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.Identity.ExactMachineMatch)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The exact Ally identity no longer matches.")));
        }

        if (!acpi.TryOpen())
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The ASUS System Control Interface driver (ATKACPI) is not installed.")));
        }

        Capability = new AllyChargeLimitCapability(acpi);
        Refresh();
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    public void Refresh()
    {
        LastObserved = Capability?.Read();
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }
}

/// <summary>What the plugin last sent to Aura. Aura cannot be read back, so this is intent, not observation.</summary>
/// <param name="Brightness">Requested brightness percentage, validated to 0-100 before writing.</param>
/// <param name="Effect">Aura animation or solid-color effect.</param>
/// <param name="Speed">Requested speed percentage, mapped to firmware bands.</param>
/// <param name="LeftColor">Left/primary packed 0xRRGGBB color.</param>
/// <param name="RightColor">Right/secondary packed 0xRRGGBB color.</param>
internal sealed record AllyLightingState(int Brightness, AuraEffect Effect, int Speed, int LeftColor, int RightColor)
{
    public static AllyLightingState Initial { get; } = new(100, AuraEffect.Solid, 50, 0xFF0000, 0xFF0000);
}

/// <summary>Aura RGB on the joystick rings.</summary>
/// <remarks>
///     HC's sequence: brightness as a feature report, then the colour messages as output reports
///     (<c>ROGAlly.cs:507-593</c>). Two different solid ring colours use HC's per-zone path
///     (<c>ApplyColorFast</c>); everything else is one all-zone message, with the right ring's colour as
///     the breathing effect's second colour.
/// </remarks>
/// <param name="aura">Shared Aura/lamp-array transport; accepted lighting choices persist beyond service release.</param>
internal sealed class LightingService(IAllyAuraHid aura) : DeviceService<AllyIdentityState>(AllyServiceIds.Lighting)
{
    private bool _dynamicLightingHandled;
    private AllyModel? _model;

    public AllyLightingState Desired { get; private set; } = AllyLightingState.Initial;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (context.Identity.Model is not { } model)
        {
            return Set(DeviceServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        _model = model;
        _dynamicLightingHandled = false;
        return await aura.IsAvailableAsync(cancellationToken).ConfigureAwait(false)
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, Missing("No ASUS collection answered Aura report 0x5D."));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Lighting is a persistent user choice; stopping never rewrites it.
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        Func<AllyLightingState, AllyLightingState> update,
        CancellationToken cancellationToken)
    {
        var wanted = update(Desired);
        if (wanted.Brightness is < 0 or > 100 || wanted.Speed is < 0 or > 100
                                              || wanted.LeftColor is < 0 or > 0xFFFFFF
                                              || wanted.RightColor is < 0 or > 0xFFFFFF)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "Lighting values are outside their range.");
        }

        try
        {
            if (_model?.DisableDynamicLighting == true && !_dynamicLightingHandled)
            {
                // One attempt per cycle; the result is traced, never retried.
                _dynamicLightingHandled = true;
                var disabled = await aura.DisableDynamicLightingAsync(cancellationToken).ConfigureAwait(false);
                DeviceTrace.Info("lighting", $"lamp array autonomous mode requested: {disabled}.");
            }

            foreach (var report in Encode(wanted))
            {
                if (report.Feature)
                {
                    await aura.SetFeatureAsync(report.Bytes, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await aura.WriteOutputAsync(report.Bytes, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            DeviceTrace.Failure("lighting", "Aura write failed", ex);
            // The previous intent is kept: nothing reads Aura back, so the device state is unknown.
            return CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException("The Aura write failed", ex));
        }

        Desired = wanted;
        return CommandResults.Applied(command);
    }

    /// <summary>The reports one lighting state needs, in HC's order.</summary>
    /// <remarks>
    ///     One colour, or any animated effect, is HC's <c>ApplyColor</c>: one all-zone message, then apply
    ///     and set (<c>ROGAlly.cs:555-572</c>). Two different solid colours are HC's <c>ApplyColorFast</c>:
    ///     four per-zone messages at the slow speed with no apply or set (<c>ROGAlly.cs:574-593</c>).
    /// </remarks>
    /// <param name="state">Previously validated desired lighting values.</param>
    /// <returns>
    ///     Ordered newly allocated reports with Feature distinguishing brightness from output reports; no hardware is
    ///     touched.
    /// </returns>
    internal static IReadOnlyList<(byte[] Bytes, bool Feature)> Encode(AllyLightingState state)
    {
        List<(byte[], bool)> reports = [(AllyProtocol.Brightness(state.Brightness), true)];
        if (state.Effect is AuraEffect.Solid && state.LeftColor != state.RightColor)
        {
            const byte speed = AllyProtocol.SpeedSlow;
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.LeftStickLeft, state.LeftColor, state.LeftColor, speed),
                false));
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.LeftStickRight, state.LeftColor, state.LeftColor, speed),
                false));
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickLeft, state.RightColor, state.RightColor,
                    speed), false));
            reports.Add((
                AllyProtocol.Color(AuraEffect.Solid, AuraZone.RightStickRight, state.RightColor, state.RightColor,
                    speed), false));
            return reports;
        }

        reports.Add((AllyProtocol.Color(state.Effect, AuraZone.All, state.LeftColor, state.RightColor,
            AllyProtocol.Speed(state.Speed)), false));
        reports.Add((AllyProtocol.Apply(), false));
        reports.Add((AllyProtocol.Set(), false));
        return reports;
    }
}
