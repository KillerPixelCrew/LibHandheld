using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Devices;

/// <summary>The IPF power path: offering, the policy service around it and the session lifetime.</summary>
public sealed class IntelPowerPathsTests : IDisposable
{
    private const string Firmware = "firmware";
    private readonly FakePolicy _policy = new();
    private readonly List<FakeSession> _sessions = [];
    private readonly IIntelPolicyControl _savedPolicy = IntelPowerPaths.PolicyControl;
    private readonly Func<bool> _savedPresent = IntelPowerPaths.IpfPresent;
    private readonly Func<IIntelIpfSession>? _savedOpen = IntelPowerPaths.OpenIpf;
    private bool _openFails;

    public IntelPowerPathsTests()
    {
        IntelPowerPaths.PolicyControl = _policy;
        IntelPowerPaths.IpfPresent = () => true;
        IntelPowerPaths.OpenIpf = () =>
        {
            if (_openFails)
            {
                throw new IOException("Ipc_SetAppIface failed with ESIF error 0x3EA.");
            }

            var session = new FakeSession();
            _sessions.Add(session);
            return session;
        };
    }

    public void Dispose()
    {
        IntelPowerPaths.PolicyControl = _savedPolicy;
        IntelPowerPaths.IpfPresent = _savedPresent;
        IntelPowerPaths.OpenIpf = _savedOpen;
    }

    [Fact]
    public async Task IpfIsOfferedOnlyWhenItsLibraryAndServiceExist()
    {
        using TemporaryDirectory directory = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var offered = new IntelPowerPaths(journal, "power", hasFirmware: true);
        offered.Acquire(pawnIo: true);
        Assert.Equal(["firmware", "pawnio", "ipf"], offered.Descriptor!.Choices.Select(choice => choice.Value));
        Assert.Equal("IPF", offered.Descriptor.Choices[2].Display.CustomText);

        IntelPowerPaths.IpfPresent = () => false;
        var absent = new IntelPowerPaths(journal, "power2", hasFirmware: true);
        absent.Acquire(pawnIo: true);
        Assert.Equal(["firmware", "pawnio"], absent.Descriptor!.Choices.Select(choice => choice.Value));
    }

    [Fact]
    public async Task IpfKeepsThePolicyServiceRunningAndOwnsOneSession()
    {
        using TemporaryDirectory directory = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var paths = new IntelPowerPaths(journal, "power", hasFirmware: true, policyOffOnFirmware: true);
        paths.Acquire(pawnIo: true);

        await paths.ResumeAsync(Firmware, CancellationToken.None);
        Assert.False(_policy.Running);

        await paths.SelectAsync(IntelPowerPath.Ipf, Firmware, CancellationToken.None);
        Assert.True(_policy.Running);
        Assert.True(paths.ViaIpf);
        Assert.False(paths.Direct);
        paths.WriteIpf(40, 45);
        Assert.Equal((40, 45), Assert.Single(_sessions).Written);

        await paths.SelectAsync(IntelPowerPath.PawnIo, Firmware, CancellationToken.None);
        Assert.True(_sessions[0].Disposed);
        Assert.False(_policy.Running);
        Assert.Throws<InvalidOperationException>(() => paths.WriteIpf(40, 45));
    }

    [Fact]
    public async Task AFailedSessionOpenReturnsToThePreviousPathAndItsPolicyState()
    {
        using TemporaryDirectory directory = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var paths = new IntelPowerPaths(journal, "power", hasFirmware: true);
        paths.Acquire(pawnIo: true);
        await paths.SelectAsync(IntelPowerPath.PawnIo, Firmware, CancellationToken.None);
        Assert.False(_policy.Running);

        _openFails = true;
        await Assert.ThrowsAsync<IOException>(() =>
            paths.SelectAsync(IntelPowerPath.Ipf, Firmware, CancellationToken.None).AsTask());

        Assert.Equal(IntelPowerPath.PawnIo, paths.Selected);
        Assert.False(_policy.Running);
        Assert.Empty(_sessions);
    }

    [Fact]
    public async Task ReleaseClosesTheSessionSoIpfDropsTheRequest()
    {
        using TemporaryDirectory directory = new();
        var journal = await DeviceRecoveryJournal.OpenAsync(directory.Root, CancellationToken.None);
        var paths = new IntelPowerPaths(journal, "power", hasFirmware: true);
        paths.Acquire(pawnIo: true);
        await paths.SelectAsync(IntelPowerPath.Ipf, Firmware, CancellationToken.None);

        await paths.ReleaseAsync(Firmware, CancellationToken.None);

        Assert.True(Assert.Single(_sessions).Disposed);
        Assert.True(_policy.Running);
        Assert.False(paths.HasUnrestored);
    }

    private sealed class FakePolicy : IIntelPolicyControl
    {
        private int _startType = 2;
        internal bool Running { get; private set; } = true;

        public WindowsServiceState? Read(string name)
        {
            return name == "ipfsvc" ? new WindowsServiceState(_startType, Running) : null;
        }

        public bool IsRunning(string name)
        {
            return name == "ipfsvc" && Running;
        }

        public void DisableAndStop(string name)
        {
            _startType = WindowsServiceControl.Disabled;
            Running = false;
        }

        public void Stop(string name)
        {
            Running = false;
        }

        public void Restore(string name, WindowsServiceState original)
        {
            _startType = original.StartType;
            Running = original.Running;
        }
    }

    private sealed class FakeSession : IIntelIpfSession
    {
        internal (int Sustained, int Boost)? Written { get; private set; }
        internal bool Disposed { get; private set; }

        public CpuPowerLimits ReadLimits()
        {
            return new CpuPowerLimits(Written?.Sustained ?? 45, Written?.Boost ?? 65, Written?.Boost ?? 65);
        }

        public void WriteLimits(int sustainedWatts, int boostWatts)
        {
            Written = (sustainedWatts, boostWatts);
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
