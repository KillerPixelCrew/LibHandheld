using System.Management;
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

    /// <summary>MSR 0x610 plus the MCHBAR mirror through KX, which loads HwRwDrv per command.</summary>
    Kx
}

/// <summary>The user's Intel power-path choice and the side effects only the direct paths need.</summary>
/// <remarks>
///     The owning power service keeps its own firmware and MSR writes and asks this which to use. This owns
///     the MCHBAR mirror (KX) and Intel's IPF/DTT policy service, both with firmware-bound recovery
///     records. On the Atlas IPF lowered MCHBAR PL1 every few seconds and pulled sustained power to 35 W
///     (2026-10-31), so it is disabled and stopped while a direct path is selected and restored when
///     Firmware is selected or ownership ends. The offered paths are decided once, at first acquisition.
/// </remarks>
internal sealed class IntelPowerPaths(DeviceRecoveryJournal journal, string serviceId, bool hasFirmware)
{
    internal const string FirmwareChoice = "firmware";
    internal const string PawnIoChoice = "pawnio";
    internal const string KxChoice = "kx";
    private static readonly string[] PolicyServices = ["ipfsvc", "esifsvc"];

    /// <summary>The Service Control Manager seam; the test assembly replaces it so no fixture touches real services.</summary>
    internal static IIntelPolicyControl PolicyControl { get; set; } = new WindowsIntelPolicyControl();
    private readonly string _mchbarRecoveryId = serviceId + ".mchbar";
    private readonly string _policyRecoveryId = serviceId + ".intel-policy";
    private IReadOnlyList<string>? _choices;
    private IntelKxMchbar? _kx;
    private string? _kxPath;
    private bool _policyRestartReported;

    /// <summary>The selected path; the default is Firmware where the model has one, otherwise PawnIO.</summary>
    internal IntelPowerPath Selected { get; private set; } = hasFirmware ? IntelPowerPath.Firmware : IntelPowerPath.PawnIo;

    /// <summary>Whether a CPU register path is selected.</summary>
    internal bool Direct => Selected != IntelPowerPath.Firmware;

    /// <summary>Fixes the offered paths on first acquisition and opens the pinned KX file without running it.</summary>
    /// <param name="kxPath">The deployed KX helper, or null.</param>
    /// <param name="pawnIo">Whether the MSR transport opened.</param>
    internal void Acquire(string? kxPath, bool pawnIo)
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
                if (KxUsable(kxPath, out var reason))
                {
                    choices.Add(KxChoice);
                    _kxPath = kxPath;
                }
                else
                {
                    DeviceTrace.Change(serviceId, "power-path", $"KX power path not offered: {reason}");
                }
            }

            _choices = choices;
            DeviceTrace.Change(serviceId, "power-path", $"Power paths offered: {string.Join(", ", choices)}.");
        }

        if (_kxPath is not null && _kx is null)
        {
            try
            {
                _kx = IntelKxMchbar.Open(_kxPath);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn(serviceId, $"KX payload unavailable: {exception.Message}");
            }
        }

        if (!Offered(Selected))
        {
            Selected = hasFirmware ? IntelPowerPath.Firmware : IntelPowerPath.PawnIo;
        }
    }

    /// <summary>The path choice, or null when only one path exists.</summary>
    internal CapabilityDescriptor? Descriptor => _choices is { Count: > 1 } choices
        ? new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.PowerBackend, Role = CapabilityRole.PowerBackend,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = "Power limit path" },
            ValueKind = CapabilityValueKind.Choice, SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile, ProfileScope = CapabilityProfileScope.GlobalOnly,
            Choices = CapabilityDescriptors.Choices(choices)
        }
        : null;

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
            KxChoice => IntelPowerPath.Kx,
            _ => (IntelPowerPath)(-1)
        };
        return Offered(path);
    }

    /// <summary>Switches path: leaving KX restores MCHBAR, Firmware restores the policy service, a direct path stops it.</summary>
    /// <remarks>The caller restores or re-applies its own MSR and firmware limits for the new path.</remarks>
    internal async ValueTask SelectAsync(IntelPowerPath path, string firmware, CancellationToken token)
    {
        if (path != IntelPowerPath.Kx)
        {
            await RestoreMchbarAsync(firmware, token).ConfigureAwait(false);
        }

        if (path == IntelPowerPath.Firmware)
        {
            await RestorePolicyAsync(firmware, token).ConfigureAwait(false);
        }
        else
        {
            await DisablePolicyAsync(firmware, token).ConfigureAwait(false);
        }

        Selected = path;
        DeviceTrace.Change(serviceId, "power-path", $"Power limit path: {Name(path)}.");
    }

    /// <summary>Puts the direct-path state back in place after acquisition: the policy service off again.</summary>
    internal async ValueTask ResumeAsync(string firmware, CancellationToken token)
    {
        if (Direct)
        {
            await DisablePolicyAsync(firmware, token).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the MCHBAR mirror when KX is selected, journaling its original first.</summary>
    /// <remarks>The dispatch callback marks the uncertain-write boundary for the caller.</remarks>
    internal async ValueTask WriteMchbarAsync(int sustainedWatts, int boostWatts, string firmware,
        CancellationToken token, Action? dispatched = null)
    {
        if (Selected != IntelPowerPath.Kx)
        {
            return;
        }

        var kx = _kx ?? throw new InvalidOperationException("The KX payload is unavailable.");
        await journal.BeginAsync<IntelMchbarOriginal>(_mchbarRecoveryId, firmware, () =>
        {
            var original = kx.ReadLimits();
            return new IntelMchbarOriginal { Sustained = original.SustainedWatts, Boost = original.BoostWatts };
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        dispatched?.Invoke();
        kx.WriteLimits(sustainedWatts, boostWatts);
    }

    /// <summary>Keeps the policy service off while a direct path is selected; logs a restart once.</summary>
    internal void Refresh()
    {
        if (!Direct)
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
                        DeviceTrace.Warn(serviceId, $"{name} was started again while a direct power path is selected; stopping it.");
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

    /// <summary>Restores MCHBAR, then the policy service, for release or a firmware-bound reconcile.</summary>
    internal async ValueTask RestoreAsync(string firmware, CancellationToken token)
    {
        await RestoreMchbarAsync(firmware, token).ConfigureAwait(false);
        await RestorePolicyAsync(firmware, token).ConfigureAwait(false);
    }

    internal bool HasUnrestored => journal.HasUnrestoredMutation(_mchbarRecoveryId)
                                   || journal.HasUnrestoredMutation(_policyRecoveryId);

    internal void Dispose()
    {
        _kx?.Dispose();
        _kx = null;
    }

    internal static string Name(IntelPowerPath path)
    {
        return path switch
        {
            IntelPowerPath.Firmware => FirmwareChoice,
            IntelPowerPath.PawnIo => PawnIoChoice,
            _ => KxChoice
        };
    }

    private bool Offered(IntelPowerPath path)
    {
        return _choices is { } choices && Enum.IsDefined(path) && choices.Contains(Name(path));
    }

    internal async ValueTask RestoreMchbarAsync(string firmware, CancellationToken token)
    {
        await journal.ReconcileAsync<IntelMchbarOriginal>(_mchbarRecoveryId, firmware, async (original, ct) =>
        {
            var kx = _kx ?? throw new InvalidOperationException("The KX payload is unavailable to restore MCHBAR.");
            kx.WriteLimits(original.Sustained, original.Boost);
            bool? verified = null;
            try
            {
                var current = kx.ReadLimits();
                verified = Math.Abs(current.SustainedWatts - original.Sustained) < 0.2
                           && Math.Abs(current.BoostWatts - original.Boost) < 0.2;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn(serviceId, $"Restored MCHBAR observation unavailable: {exception.Message}");
            }

            await journal.SetStatusAsync(_mchbarRecoveryId, DeviceRecoveryJournal.RestoreStatus(verified),
                CancellationToken.None).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
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
        DeviceTrace.Change(serviceId, "intel-policy", $"Disabled and stopped {string.Join(", ", originals.Keys)} for the direct power path.");
    }

    private async ValueTask RestorePolicyAsync(string firmware, CancellationToken token)
    {
        await journal.ReconcileAsync<IntelPolicyOriginal>(_policyRecoveryId, firmware, async (original, _) =>
        {
            foreach (var (name, service) in original.Services)
            {
                PolicyControl.Restore(name, new WindowsServiceState(service.StartType, service.Running));
            }

            await journal.SetStatusAsync(_policyRecoveryId, DeviceRecoveryStatus.RestoredUnverified,
                CancellationToken.None).ConfigureAwait(false);
            DeviceTrace.Change(serviceId, "intel-policy", $"Restored {string.Join(", ", original.Services.Keys)}.");
        }, token).ConfigureAwait(false);
    }

    private static bool KxUsable(string? path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            reason = "the KX helper is not deployed.";
            return false;
        }

        if (MemoryIntegrityRunning())
        {
            reason = "Memory Integrity is running, so Windows refuses its HwRwDrv driver.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Whether HVCI is running in this boot; HVCI always enforces the vulnerable-driver blocklist.</summary>
    private static bool MemoryIntegrityRunning()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard",
                "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["SecurityServicesRunning"] is uint[] running && running.Contains(2u))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException
                                              or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
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

/// <summary>Firmware-bound original MCHBAR PL1/PL2 in watts.</summary>
internal sealed record IntelMchbarOriginal
{
    public double Sustained { get; init; }
    public double Boost { get; init; }
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
