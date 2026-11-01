using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.MsiClaw;

/// <summary>The Claw's PawnIO power path beside its WMI firmware path.</summary>
/// <remarks>
///     With a direct path selected the engine opens the WMI pair to the model maximum once, so the firmware
///     does not cap the registers, and sends each PL1/PL2 here: MSR 0x610 through PawnIO. The MSR original is journaled under the BIOS binding like the WMI pair. Built from HC's
///     shared Intel register path; no Claw hardware pass covers the direct paths yet.
/// </remarks>
internal sealed class ClawDirectPowerService(
    DeviceRecoveryJournal journal,
    Func<ICpuPowerTransport>? openMsr) : DeviceService<ClawIdentityState>(ClawServiceIds.Power + ".direct")
{
    private const string MsrRecoveryId = ClawServiceIds.Power + ".direct.msr";
    private ICpuPowerTransport? _msr;

    internal IntelPowerPaths Paths { get; } = new(journal, ClawServiceIds.Power + ".direct", hasFirmware: true);

    /// <summary>Whether PL1/PL2 go to the CPU registers instead of WMI.</summary>
    internal bool Direct => _msr is not null && Paths.Direct;

    /// <summary>Whether PL1/PL2 bypass WMI, through MSR 0x610 or IPF.</summary>
    internal bool Active => Direct || Paths.ViaIpf;

    /// <summary>Whether the WMI pair was opened to the model maximum in this ownership.</summary>
    internal bool FirmwareOpened { get; set; }

    /// <summary>Opens the MSR transport and fixes the offered paths; start calls it before the surface is built.</summary>
    internal void Prepare()
    {
        if (_msr is null && openMsr is not null)
        {
            try
            {
                _msr = openMsr();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn(ServiceId, $"PawnIO MSR power path unavailable: {exception.Message}");
            }
        }

        Paths.Acquire(_msr is not null);
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        Prepare();
        var binding = Binding(context.Identity);
        await journal.ReconcileAsync<ClawMsrOriginal>(MsrRecoveryId, binding, RestoreMsrAsync, cancellationToken)
            .ConfigureAwait(false);
        await Paths.ResumeAsync(binding, cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        Paths.Refresh();
        return ValueTask.CompletedTask;
    }

    /// <summary>Writes PL1/PL2 to MSR 0x610; journals the MSR original first.</summary>
    internal async ValueTask WriteAsync(int sustained, int boost, string binding, Action dispatched,
        CancellationToken token)
    {
        if (Paths.ViaIpf)
        {
            token.ThrowIfCancellationRequested();
            dispatched();
            Paths.WriteIpf(sustained, boost);
            return;
        }

        var msr = _msr ?? throw new InvalidOperationException("The PawnIO MSR path is unavailable.");
        await journal.BeginAsync<ClawMsrOriginal>(MsrRecoveryId, binding, () =>
        {
            var original = msr.ReadLimits();
            return new ClawMsrOriginal { Sustained = original.SustainedWatts, Boost = original.BoostWatts };
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        dispatched();
        msr.WriteLimits(new CpuPowerLimits(sustained, boost, boost));
    }

    /// <summary>Returns MSR 0x610 to its journaled original, for the Firmware path or release.</summary>
    internal ValueTask RestoreMsrAsync(string binding, CancellationToken token)
    {
        return journal.ReconcileAsync<ClawMsrOriginal>(MsrRecoveryId, binding, RestoreMsrAsync, token);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var binding = Binding(context.Identity);
        try
        {
            await RestoreMsrAsync(binding, cancellationToken).ConfigureAwait(false);
            await Paths.ReleaseAsync(binding, cancellationToken).ConfigureAwait(false);
            return journal.HasUnrestoredMutation(MsrRecoveryId) || Paths.HasUnrestored
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The original CPU power registers or Intel policy service were not restored."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    internal override ValueTask DisposeAsync()
    {
        _msr?.Dispose();
        _msr = null;
        Paths.CloseIpf();
        FirmwareOpened = false;
        return ValueTask.CompletedTask;
    }

    /// <summary>The BIOS binding, or one fixed name when the firmware identity is unknown.</summary>
    internal static string Binding(ClawIdentityState identity)
    {
        return identity.RecoveryBinding ?? "msi-claw";
    }

    private async ValueTask RestoreMsrAsync(ClawMsrOriginal original, CancellationToken token)
    {
        var msr = _msr ?? throw new InvalidOperationException("The PawnIO MSR path is unavailable to restore.");
        token.ThrowIfCancellationRequested();
        msr.WriteLimits(new CpuPowerLimits(original.Sustained, original.Boost, original.Boost));
        await journal.MarkRestoredAsync(MsrRecoveryId, CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>Firmware-bound original MSR 0x610 PL1/PL2 in watts.</summary>
internal sealed record ClawMsrOriginal
{
    public double Sustained { get; init; }
    public double Boost { get; init; }
}
