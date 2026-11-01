using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Devices;

/// <summary>Which hardware path carries an Intel handheld's PL1/PL2.</summary>
internal enum IntelPowerPath
{
    /// <summary>The model's own firmware interface (WMI, EC); the CPU registers stay as firmware set them.</summary>
    Firmware,

    /// <summary>MSR 0x610 through the signed PawnIO IntelMSR module.</summary>
    PawnIo,

    /// <summary>
    ///     PL1/PL2 requested from Intel's IPF over its elevated client session; IPF itself applies them, so
    ///     its service stays running and Memory Integrity can stay on.
    /// </summary>
    Ipf
}

/// <summary>The user's Intel power-path choice and the Intel policy-service guard.</summary>
/// <remarks>
///     The owning power service keeps its own firmware and MSR writes and asks this which to use. This owns
///     Intel's IPF/DTT policy service with a firmware-bound recovery record. On the Atlas IPF lowered MCHBAR
///     PL1 every few seconds and pulled sustained power to 35 W (2026-10-31), and PawnIO cannot write that
///     mirror. IPF is therefore disabled and stopped while the PawnIO path is selected, and on the firmware
///     path too where the model asks for it, and restored otherwise or when ownership ends. The offered
///     paths are decided once, at first acquisition.
/// </remarks>
/// <param name="journal">Holds the policy service's firmware-bound original.</param>
/// <param name="serviceId">The owning power service, which names the recovery record and log lines.</param>
/// <param name="hasFirmware">Whether the model has its own firmware power path.</param>
/// <param name="policyOffOnFirmware">Whether IPF also stays off on the firmware path (the Atlas EC path).</param>
/// <param name="preferIpf">Whether IPF is the default path where it is installed (the Atlas).</param>
internal sealed class IntelPowerPaths(
    DeviceRecoveryJournal journal,
    string serviceId,
    bool hasFirmware,
    bool policyOffOnFirmware = false,
    bool preferIpf = false)
{
    internal const string FirmwareChoice = "firmware";
    internal const string PawnIoChoice = "pawnio";
    internal const string IpfChoice = "ipf";
    private static readonly string[] PolicyServices = ["ipfsvc", "esifsvc"];

    /// <summary>The Service Control Manager seam; the test assembly replaces it so no fixture touches real services.</summary>
    internal static IIntelPolicyControl PolicyControl { get; set; } = new WindowsIntelPolicyControl();
    private readonly string _policyRecoveryId = serviceId + ".intel-policy";
    private IReadOnlyList<string>? _choices;
    private bool _policyRestartReported;
    private IIntelIpfSession? _ipf;

    /// <summary>Opens the IPF session; the test assembly replaces it so no fixture loads Intel's library.</summary>
    internal static Func<IIntelIpfSession>? OpenIpf { get; set; } = IntelIpfTransport.Open;

    /// <summary>Whether IPF's client library is installed; the test assembly replaces it.</summary>
    internal static Func<bool> IpfPresent { get; set; } = IntelIpfTransport.Present;

    /// <summary>The selected path; the default is Firmware where the model has one, otherwise PawnIO.</summary>
    internal IntelPowerPath Selected { get; private set; } = hasFirmware ? IntelPowerPath.Firmware : IntelPowerPath.PawnIo;

    /// <summary>Whether the PawnIO MSR path is selected.</summary>
    internal bool Direct => Selected == IntelPowerPath.PawnIo;

    /// <summary>Whether IPF carries the limits.</summary>
    internal bool ViaIpf => Selected == IntelPowerPath.Ipf;

    // IPF must run for its own path; it stays off on PawnIO, and on Firmware where the model asks.
    private bool PolicyOff => Direct || (Selected == IntelPowerPath.Firmware && policyOffOnFirmware);

    /// <summary>The path choice, fixed at first acquisition; null before it or when only one path exists.</summary>
    internal CapabilityDescriptor? Descriptor { get; private set; }

    internal bool HasUnrestored => journal.HasUnrestoredMutation(_policyRecoveryId);

    /// <summary>Fixes the offered paths on first acquisition.</summary>
    /// <param name="pawnIo">Whether the MSR transport opened.</param>
    internal void Acquire(bool pawnIo)
    {
        if (_choices is null)
        {
            List<string> choices = [];
            if (hasFirmware)
            {
                choices.Add(FirmwareChoice);
            }

            if (pawnIo)
            {
                choices.Add(PawnIoChoice);
            }

            if (IpfPresent() && PolicyControl.Read("ipfsvc") is not null)
            {
                choices.Add(IpfChoice);
            }

            _choices = choices;
            Descriptor = choices.Count > 1
                ? CapabilityDescriptors.Choice(CapabilityIds.PowerBackend, CapabilityRole.PowerBackend,
                    CapabilityDescriptors.Label("Power limit path"), CapabilityDescriptors.Choices(choices),
                    read: true, write: true) with { ProfileScope = CapabilityProfileScope.GlobalOnly }
                : null;
            DeviceTrace.Change(serviceId, "power-path", $"Power paths offered: {string.Join(", ", choices)}.");
            if (preferIpf && choices.Contains(IpfChoice))
            {
                // The model's default where IPF is installed; a saved choice still replaces it.
                Selected = IntelPowerPath.Ipf;
            }
        }

        if (!Offered(Selected))
        {
            Selected = Fallback;
        }
    }

    private IntelPowerPath Fallback => hasFirmware ? IntelPowerPath.Firmware : IntelPowerPath.PawnIo;

    /// <summary>Requests PL1/PL2 from IPF; sent once, never retried.</summary>
    internal void WriteIpf(int sustainedWatts, int boostWatts)
    {
        var ipf = _ipf ?? throw new InvalidOperationException("The IPF session is not open.");
        ipf.WriteLimits(sustainedWatts, boostWatts);
    }

    /// <summary>Withdraws WSGM's IPF request and closes the session; IPF's own policy value applies again.</summary>
    internal void CloseIpf()
    {
        _ipf?.Dispose();
        _ipf = null;
    }

    /// <summary>Closes the IPF session, then restores the policy service, when ownership ends.</summary>
    internal ValueTask ReleaseAsync(string firmware, CancellationToken token)
    {
        CloseIpf();
        return RestoreAsync(firmware, token);
    }

    internal CapabilityState State(bool owned, CapabilityReason? reason)
    {
        return ProviderControls.State(CapabilityIds.PowerBackend, owned, CapabilityValue.Choice(Name(Selected)),
            DateTimeOffset.UtcNow, reason);
    }

    /// <summary>Parses a requested choice that this model offers.</summary>
    internal bool TryParse(CapabilityCommand command, out IntelPowerPath path)
    {
        path = command.RequestedValue?.ChoiceValue switch
        {
            FirmwareChoice => IntelPowerPath.Firmware,
            PawnIoChoice => IntelPowerPath.PawnIo,
            IpfChoice => IntelPowerPath.Ipf,
            _ => (IntelPowerPath)(-1)
        };
        return Offered(path);
    }

    /// <summary>Switches path and turns the policy service off or back on for it.</summary>
    /// <remarks>The caller restores or re-applies its own MSR and firmware limits for the new path.</remarks>
    /// <remarks>A failed IPF session open returns to the previous path and its policy state, then rethrows.</remarks>
    internal async ValueTask SelectAsync(IntelPowerPath path, string firmware, CancellationToken token)
    {
        var previous = Selected;
        if (path != IntelPowerPath.Ipf)
        {
            CloseIpf();
        }

        Selected = path;
        try
        {
            await ApplyAsync(firmware, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (path == IntelPowerPath.Ipf && exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(serviceId, $"IPF power path unavailable, keeping {Name(previous)}: {exception.Message}");
            // Finish the revert even when the command's deadline cancelled the open.
            Selected = previous;
            await ApplyAsync(firmware, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        DeviceTrace.Change(serviceId, "power-path", $"Power limit path: {Name(path)}.");
    }

    /// <summary>Puts the selected path's policy-service state in place after acquisition, and the IPF session.</summary>
    /// <remarks>
    ///     An IPF session that cannot open here (a default or saved choice, not a user's switch) falls back to
    ///     the model's default path so the power service still starts; the reason is logged.
    /// </remarks>
    internal async ValueTask ResumeAsync(string firmware, CancellationToken token)
    {
        try
        {
            await ApplyAsync(firmware, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (ViaIpf && exception is not OutOfMemoryException
                                                 && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(serviceId, $"IPF power path unavailable, using {Name(Fallback)}: {exception.Message}");
            CloseIpf();
            Selected = Fallback;
            await ApplyAsync(firmware, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyAsync(string firmware, CancellationToken token)
    {
        if (PolicyOff)
        {
            await DisablePolicyAsync(firmware, token).ConfigureAwait(false);
            return;
        }

        await RestoreAsync(firmware, token).ConfigureAwait(false);
        if (ViaIpf && _ipf is null)
        {
            await WaitForPolicyAsync(token).ConfigureAwait(false);
            _ipf = (OpenIpf ?? throw new InvalidOperationException("No IPF session opener."))();
            DeviceTrace.Change(serviceId, "power-path", "IPF elevated session open; IPF applies the power limits.");
        }
    }

    // A restored ipfsvc takes a few seconds before its elevated listener accepts a session.
    private static async ValueTask WaitForPolicyAsync(CancellationToken token)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!PolicyControl.IsRunning("ipfsvc"))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new IOException("ipfsvc did not start, so IPF cannot apply the power limits.");
            }

            await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    /// <summary>Keeps the policy service off while the selected path needs it off; logs a restart once.</summary>
    internal void Refresh()
    {
        if (!PolicyOff)
        {
            return;
        }

        foreach (var name in PolicyServices)
        {
            try
            {
                if (PolicyControl.IsRunning(name))
                {
                    if (!_policyRestartReported)
                    {
                        _policyRestartReported = true;
                        DeviceTrace.Warn(serviceId, $"{name} was started again while WSGM owns the power limits; stopping it.");
                    }

                    PolicyControl.Stop(name);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn(serviceId, $"{name} could not be kept off: {exception.Message}");
            }
        }
    }

    /// <summary>Restores the policy service for release or a firmware-bound reconcile.</summary>
    internal async ValueTask RestoreAsync(string firmware, CancellationToken token)
    {
        await journal.ReconcileAsync<IntelPolicyOriginal>(_policyRecoveryId, firmware, async (original, _) =>
        {
            foreach (var (name, service) in original.Services)
            {
                PolicyControl.Restore(name, new WindowsServiceState(service.StartType, service.Running));
            }

            await journal.MarkRestoredAsync(_policyRecoveryId, CancellationToken.None).ConfigureAwait(false);
            DeviceTrace.Change(serviceId, "intel-policy", $"Restored {string.Join(", ", original.Services.Keys)}.");
        }, token).ConfigureAwait(false);
    }

    internal static string Name(IntelPowerPath path)
    {
        return path switch
        {
            IntelPowerPath.Firmware => FirmwareChoice,
            IntelPowerPath.Ipf => IpfChoice,
            _ => PawnIoChoice
        };
    }

    /// <summary>Whether the IPF path is offered on this device.</summary>
    internal bool IpfOffered => Offered(IntelPowerPath.Ipf);

    private bool Offered(IntelPowerPath path)
    {
        return _choices is { } choices && Enum.IsDefined(path) && choices.Contains(Name(path));
    }

    private async ValueTask DisablePolicyAsync(string firmware, CancellationToken token)
    {
        var originals = new Dictionary<string, IntelPolicyServiceOriginal>();
        foreach (var name in PolicyServices)
        {
            if (PolicyControl.Read(name) is { } state)
            {
                originals[name] = new IntelPolicyServiceOriginal { StartType = state.StartType, Running = state.Running };
            }
        }

        if (originals.Count == 0)
        {
            return;
        }

        await journal.BeginAsync(_policyRecoveryId, firmware, new IntelPolicyOriginal { Services = originals }, token)
            .ConfigureAwait(false);
        foreach (var name in originals.Keys)
        {
            try
            {
                PolicyControl.DisableAndStop(name);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn(serviceId, $"{name} could not be disabled: {exception.Message}");
            }
        }

        _policyRestartReported = false;
        DeviceTrace.Change(serviceId, "intel-policy",
            $"Disabled and stopped {string.Join(", ", originals.Keys)} while WSGM owns the power limits.");
    }
}

/// <summary>Start-type and run-state control of Intel's policy services.</summary>
internal interface IIntelPolicyControl
{
    WindowsServiceState? Read(string name);
    bool IsRunning(string name);
    void DisableAndStop(string name);
    void Stop(string name);
    void Restore(string name, WindowsServiceState original);
}

internal sealed class WindowsIntelPolicyControl : IIntelPolicyControl
{
    public WindowsServiceState? Read(string name) => WindowsServiceControl.Read(name);
    public bool IsRunning(string name) => WindowsServiceControl.IsRunning(name);
    public void DisableAndStop(string name) => WindowsServiceControl.DisableAndStop(name);
    public void Stop(string name) => WindowsServiceControl.Stop(name);
    public void Restore(string name, WindowsServiceState original) => WindowsServiceControl.Restore(name, original);
}

/// <summary>Firmware-bound original start type and run state of Intel's IPF/DTT policy services.</summary>
internal sealed record IntelPolicyOriginal
{
    public Dictionary<string, IntelPolicyServiceOriginal> Services { get; init; } = [];
}

internal sealed record IntelPolicyServiceOriginal
{
    public int StartType { get; init; }
    public bool Running { get; init; }
}
