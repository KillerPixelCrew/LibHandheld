using System.Diagnostics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Reads the separate vendor and QAM collections without changing controller firmware modes.</summary>
/// <remarks>
///     Guide and rear buttons share one vendor collection, while front-right/QAM uses another.
///     Both must belong to the same physical controller location. Ordinary pad input stays in the
///     XInput source; volume and firmware keyboard holds have other sources and are not swallowed here.
///     AcerControllerModeService owns the Combination mode required for native rear notifications.
///     Callback publication happens on reader/timer threads and must return without blocking.
/// </remarks>
internal sealed class AcerButtonSource : IProfiledOemSource
{
    private readonly HidReportSource _vendor = new();
    private readonly HidReportSource _qam = new();
    private AcerButtonState? _state;
    private HidUsageReader? _dinputDecoder;
    private bool _dinput;

    /// <summary>Semantic front/rear controls; action assignments and companion-app suppression belong to the host.</summary>
    internal static IReadOnlyList<OemControlDescriptor> Controls { get; } =
        new[]
        {
            ("guide", "Guide / Task", CanonicalButtons.Guide, OemControlPlacement.Front),
            ("qam", "Predator / Quick Access", CanonicalButtons.QuickAccess, OemControlPlacement.Front),
            // ControllerButton describes only the extra Guide/QAM forwarding channel. Rear bits
            // are already carried by AcerButtonState through the canonical controller sample.
            ("m1", "M1", CanonicalButtons.None, OemControlPlacement.Rear),
            ("m2", "M2", CanonicalButtons.None, OemControlPlacement.Rear)
        }.Select(control => new OemControlDescriptor
        {
            ControlId = control.Item1,
            Display = new CapabilityDisplay { Key = DisplayKey.Custom, CustomText = control.Item2 },
            ControllerButton = control.Item3,
            Placement = control.Item4,
            RequiresControllerAcquisition = true
        }).ToArray();

    public bool IsConnected => _vendor.IsConnected && (_dinput || _qam.IsConnected);

    /// <summary>Opens one exact vendor/QAM pair and starts allocation-free report handlers for their measured lengths.</summary>
    /// <remarks>Absent/ambiguous or mismatched locations return false. Partial startup tears down both readers and held edges.</remarks>
    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dinput = HidDevices.Unique(HidDevices.Enumerate(0x0502, [0xF90A]),
            item => item is { ReleaseNumber: 0x0100, UsagePage: 1, Usage: 5, InputLength: 64, OutputLength: 32 });
        if (dinput is not null)
        {
            _dinput = true;
            _state = new AcerButtonState(publish);
            var stream = HidDevices.OpenStream(dinput);
            try
            {
                _dinputDecoder = new HidUsageReader(stream.SafeFileHandle);
                if (_vendor.Start(stream, dinput.InputLength, HandleDInputReport,
                        () => { _state?.ReleaseVendor(); _state?.ReleaseQam(); }, token))
                {
                    return true;
                }
            }
            catch
            {
                stream.Dispose();
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
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

    /// <summary>Releases held OEM edges and stops both readers, even when the caller cancels the first stop.</summary>
    /// <remarks>Disposing state disables new publication and the Guide timer before native handles are released.</remarks>
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
        _dinputDecoder?.Dispose();
        _dinputDecoder = null;
        _dinput = false;
    }

    private void HandleDInputReport(ReadOnlySpan<byte> report)
    {
        if (report.Length == 64 && report[0] == 1 && _dinputDecoder is { } decoder)
        {
            _state?.HandleDInputButtons(decoder.Usages(report, 9));
        }
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }
}

/// <summary>Keeps unrelated vendor reports from releasing rear buttons and bounds release-less Guide events.</summary>
/// <remarks>
///     One lock serializes vendor input, QAM input, the synthetic Guide-release timer and disposal.
///     The bitset deduplicates transitions; unknown reports leave existing state alone. Guide is a
///     short synthetic pulse, not a measured physical hold or a source for the keyboard's long press.
/// </remarks>
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

    /// <summary>Routes the DInput PS/camera and rear usages through the same OEM action owner.</summary>
    /// <remarks>The 17-button descriptor and OEM PS/camera/FL1/FR1 naming supply this mapping;
    /// physical front/rear mapping acceptance remains pending.</remarks>
    internal void HandleDInputButtons(ReadOnlySpan<ushort> usages)
    {
        lock (_gate)
        {
            if (_publish is null)
            {
                return;
            }
            Emit("guide", CanonicalButtons.Guide, usages.Contains((ushort)13));
            Emit("qam", CanonicalButtons.QuickAccess, usages.Contains((ushort)14));
            Emit("m2", CanonicalButtons.RearPaddle2, usages.Contains((ushort)15));
            Emit("m1", CanonicalButtons.RearPaddle1, usages.Contains((ushort)16));
        }
    }

    /// <summary>Updates M1/M2 only for report 9A, or extends Guide's 200 ms latch for its exact 9E notification.</summary>
    /// <remarks>Extending an existing latch avoids a second press edge while ensuring the latest event gets a full pulse.</remarks>
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

    /// <summary>Publishes real front-right down/up transitions from the separate three-byte QAM lane.</summary>
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

    /// <summary>Cancels the Guide latch and emits release edges for every held vendor-lane control on fault/stop.</summary>
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

    /// <summary>Emits the QAM release on source loss, independently of vendor-lane state.</summary>
    internal void ReleaseQam()
    {
        lock (_gate)
        {
            Emit("qam", CanonicalButtons.QuickAccess, down: false);
        }
    }

    /// <summary>Releases all held edges once, disables further callbacks and disposes the Guide timer under the same lock.</summary>
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

    /// <summary>Uses a monotonic deadline so a late callback cannot release a pulse extended by a newer notification.</summary>
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

    /// <summary>Updates held state and publishes only a changed edge; callers hold the state lock.</summary>
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
