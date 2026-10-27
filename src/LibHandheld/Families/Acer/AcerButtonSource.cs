using System.Diagnostics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Reads the separate vendor and QAM collections without changing controller firmware modes.</summary>
internal sealed class AcerButtonSource : IProfiledOemSource
{
    private readonly HidReportSource _vendor = new();
    private readonly HidReportSource _qam = new();
    private AcerButtonState? _state;

    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } =
        new[]
        {
            ("guide", "Guide / Task", CanonicalButtons.Guide, OemControlPlacement.Front),
            ("qam", "Predator / Quick Access", CanonicalButtons.QuickAccess, OemControlPlacement.Front),
            ("m1", "M1", CanonicalButtons.RearPaddle1, OemControlPlacement.Rear),
            ("m2", "M2", CanonicalButtons.RearPaddle2, OemControlPlacement.Rear)
        }.Select(control => new OemControlDescriptor
        {
            ControlId = control.Item1,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = control.Item2 },
            ControllerButton = control.Item3,
            Placement = control.Item4,
            RequiresControllerAcquisition = true
        }).ToArray();

    public bool IsConnected => _vendor.IsConnected && _qam.IsConnected;

    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var collections = HidDevices.Enumerate(0x0502, [0xF901]).ToArray();
        var vendor = HidDevices.Unique(collections, AcerInputProtocol.IsVendorCollection);
        var qam = HidDevices.Unique(collections, AcerInputProtocol.IsQamCollection);
        if (vendor is null || qam is null
                           || !HidDevices.SamePhysicalLocation(vendor.PhysicalLocation, qam.PhysicalLocation))
        {
            return false;
        }

        var state = new AcerButtonState(publish);
        _state = state;
        try
        {
            if (_vendor.Start(HidDevices.OpenStream(vendor), vendor.InputLength,
                    state.HandleVendorReport, state.ReleaseVendor, token)
                && _qam.Start(HidDevices.OpenStream(qam), qam.InputLength,
                    state.HandleQamReport, state.ReleaseQam, token))
            {
                return true;
            }

            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken token)
    {
        // Disable publication first, including the Guide release timer. Both readers receive stop
        // even if the caller's wait is cancelled.
        _state?.Dispose();
        try
        {
            await _vendor.StopAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await _qam.StopAsync(token).ConfigureAwait(false);
        }

        _state = null;
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }
}

/// <summary>Keeps unrelated vendor reports from releasing rear buttons and bounds release-less Guide events.</summary>
internal sealed class AcerButtonState : IDisposable
{
    private readonly Lock _gate = new();
    private readonly OemButtonLatch _guidePulse = new();
    private readonly Timer _releaseGuide;
    private Action<string, CanonicalButtons, bool>? _publish;
    private CanonicalButtons _held;

    internal AcerButtonState(Action<string, CanonicalButtons, bool> publish)
    {
        _publish = publish;
        _releaseGuide = new Timer(_ => ReleaseGuide(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal void HandleVendorReport(ReadOnlySpan<byte> report)
    {
        lock (_gate)
        {
            if (_publish is null)
            {
                return;
            }

            if (AcerInputProtocol.TryDecodeRear(report, out var rear))
            {
                Emit("m1", CanonicalButtons.RearPaddle1, (rear & CanonicalButtons.RearPaddle1) != 0);
                Emit("m2", CanonicalButtons.RearPaddle2, (rear & CanonicalButtons.RearPaddle2) != 0);
            }
            else if (AcerInputProtocol.IsGuidePress(report))
            {
                _guidePulse.Pulse(OemButtonLatch.HoldDuration);
                Emit("guide", CanonicalButtons.Guide, down: true);
                _releaseGuide.Change(OemButtonLatch.HoldDuration, Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal void HandleQamReport(ReadOnlySpan<byte> report)
    {
        lock (_gate)
        {
            if (_publish is not null && AcerInputProtocol.TryDecodeQam(report, out var pressed))
            {
                Emit("qam", CanonicalButtons.QuickAccess, pressed);
            }
        }
    }

    internal void ReleaseVendor()
    {
        lock (_gate)
        {
            if (_publish is null)
            {
                return;
            }

            _guidePulse.Clear();
            _releaseGuide.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Emit("guide", CanonicalButtons.Guide, down: false);
            Emit("m1", CanonicalButtons.RearPaddle1, down: false);
            Emit("m2", CanonicalButtons.RearPaddle2, down: false);
        }
    }

    internal void ReleaseQam()
    {
        lock (_gate)
        {
            Emit("qam", CanonicalButtons.QuickAccess, down: false);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_publish is null)
            {
                return;
            }

            ReleaseVendor();
            ReleaseQam();
            _publish = null;
            _releaseGuide.Dispose();
        }
    }

    private void ReleaseGuide()
    {
        lock (_gate)
        {
            if (_publish is null)
            {
                return;
            }

            var remaining = _guidePulse.PulseDeadline - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                _releaseGuide.Change(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency),
                    Timeout.InfiniteTimeSpan);
                return;
            }

            Emit("guide", CanonicalButtons.Guide, down: false);
        }
    }

    private void Emit(string id, CanonicalButtons button, bool down)
    {
        if (((_held & button) != 0) == down)
        {
            return;
        }

        _held = down ? _held | button : _held & ~button;
        _publish?.Invoke(id, button, down);
    }
}
