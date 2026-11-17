using System.Diagnostics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Reads the separate vendor and QAM collections without changing controller firmware modes.</summary>
/// <remarks>
///     Guide and rear buttons share one vendor collection, while front-right/QAM uses another.
///     Both must belong to the same physical controller location. Ordinary pad input stays in the
///     XInput source; volume and firmware keyboard holds have other sources and are not swallowed here.
///     AcerControllerModeService owns the Combination mode required for native rear notifications. That
///     mode freezes the XInput report while a rear key is held; this source then supplies the rebuilt pad
///     to the XInput reader (<see cref="IXInputPadSubstitute" />).
///     Callback publication happens on reader/timer threads and must return without blocking.
/// </remarks>
internal sealed class AcerButtonSource : IProfiledOemSource, IHidButtonObserver, IXInputPadSubstitute
{
    private readonly HidReportSource _vendor = new();
    private readonly HidReportSource _qam = new();
    private AcerButtonState? _state;
    // DInput mode: edges come from the controller's HID reader through OnUsages, not a reader of ours.
    private volatile bool _dinput;
    private volatile bool _hidLost;

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
        }.Select(control => CapabilityDescriptors.OemControl(control.Item1, control.Item2, control.Item4,
            button: control.Item3)).ToArray();

    public bool IsConnected => _dinput ? !_hidLost : _vendor.IsConnected && _qam.IsConnected;

    /// <summary>Opens one exact vendor/QAM pair and starts allocation-free report handlers for their measured lengths.</summary>
    /// <remarks>Absent/ambiguous or mismatched locations return false. Partial startup tears down both readers and held edges.</remarks>
    public async ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var dinput = HidDevices.Unique(HidDevices.Enumerate(0x0502, [0xF90A]),
            item => item is { ReleaseNumber: 0x0100 or 0x0105, UsagePage: 1, Usage: 5, InputLength: 64, OutputLength: 32 });
        if (dinput is not null)
        {
            // The controller's HID reader already decodes every gamepad report; it hands the usages to
            // OnUsages instead of this source opening the collection a second time.
            _state = new AcerButtonState(publish);
            _hidLost = false;
            _dinput = true;
            return true;
        }
        var collections = HidDevices.Enumerate(0x0502, [0xF901, AcerControllerTransport.Atlas7ProductId]).ToArray();
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
        _dinput = false;
    }

    /// <inheritdoc />
    public void OnUsages(ReadOnlySpan<ushort> usages)
    {
        if (_dinput)
        {
            _state?.HandleDInputButtons(usages);
        }
    }

    /// <inheritdoc />
    /// <remarks>Only the XInput topology freezes; DInput carries the rear keys in its own report.</remarks>
    public bool TrySubstitute(uint packet, in StandardXInput.Gamepad polled, out StandardXInput.Gamepad pad)
    {
        if (!_dinput && _state is { } state)
        {
            return state.TrySubstitute(packet, polled, out pad);
        }

        pad = default;
        return false;
    }

    /// <inheritdoc />
    public void OnHidDisconnected()
    {
        if (_dinput)
        {
            _hidLost = true;
            _state?.ReleaseVendor();
            _state?.ReleaseQam();
        }
    }

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }
}

/// <summary>Keeps unrelated vendor reports from releasing rear buttons and bounds release-less Guide events.</summary>
/// <remarks>
///     One lock serializes vendor input, QAM input, the synthetic Guide-release timer, the XInput reader's
///     substitution and disposal. The bitset deduplicates transitions; unknown reports leave existing state
///     alone. Guide is a short synthetic pulse, not a measured physical hold or a source for the keyboard's
///     long press.
///     <para>
///         While a rear key is held, Combination mode stops the XInput report and sends the pad in
///         reports 9A (key codes) and 9B (sticks and triggers). The pad is rebuilt from them over the
///         frozen XInput state: its buttons stay, the named keys join them, and the first 9B replaces its
///         sticks and triggers. The rebuilt pad stays in place after the release until XInput reports a
///         new packet, or for <see cref="Handover" /> at most, so the frozen state is never republished.
///     </para>
/// </remarks>
internal sealed class AcerButtonState : IDisposable
{
    /// <summary>The longest the rebuilt pad outlives a release while XInput has not reported again.</summary>
    private static readonly long Handover = Stopwatch.Frequency / 10;

    private readonly Lock _gate = new();
    private readonly OemButtonLatch _guidePulse = new();
    private readonly Timer _releaseGuide;
    private Action<string, CanonicalButtons, bool>? _publish;
    private CanonicalButtons _held;
    private AcerAnalog _analog;
    private bool _analogSeen;
    private ushort _combination;
    private CanonicalButtons _combinationFront;
    private StandardXInput.Gamepad _frozen;
    private uint _frozenPacket;
    private bool _packetKnown;
    private long _releasedAt;
    private bool _substituting;

    internal AcerButtonState(Action<string, CanonicalButtons, bool> publish)
    {
        _publish = publish;
        _releaseGuide = new Timer(_ => ReleaseGuide(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Routes the DInput PS/camera and rear usages through the same OEM action owner.</summary>
    /// <remarks>2026-10-28 capture: rear-right is usage 16 (M1), rear-left usage 17 (M2). Guide/QAM
    /// 13/14 come from the descriptor naming; the front keys produced no gamepad report in that capture.</remarks>
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
            Emit("m2", CanonicalButtons.RearPaddle1, usages.Contains((ushort)17));
            Emit("m1", CanonicalButtons.RearPaddle2, usages.Contains((ushort)16));
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
                Emit("m1", CanonicalButtons.RearPaddle2, (rear & CanonicalButtons.RearPaddle2) != 0);
                Emit("m2", CanonicalButtons.RearPaddle1, (rear & CanonicalButtons.RearPaddle1) != 0);
                if (rear != CanonicalButtons.None)
                {
                    _substituting = true;
                    _releasedAt = 0;
                    _combination = AcerInputProtocol.DecodeCombinationButtons(report);
                    EmitCombinationFront(AcerInputProtocol.DecodeCombinationFront(report));
                }
                else if (_substituting && _releasedAt == 0)
                {
                    _releasedAt = Stopwatch.GetTimestamp();
                    _combination = 0;
                    EmitCombinationFront(CanonicalButtons.None);
                }
            }
            else if (AcerInputProtocol.TryDecodeAnalog(report, out var analog))
            {
                _analog = analog;
                _analogSeen = true;
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
            EndSubstitution();
            Emit("guide", CanonicalButtons.Guide, down: false);
            Emit("m1", CanonicalButtons.RearPaddle2, down: false);
            Emit("m2", CanonicalButtons.RearPaddle1, down: false);
        }
    }

    /// <summary>Rebuilds the pad Combination mode withholds from XInput, from a rear press until XInput resumes.</summary>
    /// <param name="packet">XInput's packet number for this poll.</param>
    /// <param name="polled">What XInput reports: frozen while a rear key is held.</param>
    /// <param name="pad">The rebuilt pad.</param>
    /// <returns>Whether <paramref name="pad" /> replaces the polled one.</returns>
    /// <remarks>Called by the XInput reader every poll; it takes the state lock and allocates nothing.</remarks>
    internal bool TrySubstitute(uint packet, in StandardXInput.Gamepad polled, out StandardXInput.Gamepad pad)
    {
        lock (_gate)
        {
            pad = default;
            if (_publish is null || !_substituting)
            {
                return false;
            }

            if (!_packetKnown)
            {
                _packetKnown = true;
                _frozenPacket = packet;
                _frozen = polled;
            }
            else if (_releasedAt != 0
                     && (packet != _frozenPacket || Stopwatch.GetTimestamp() - _releasedAt > Handover))
            {
                EndSubstitution();
                return false;
            }

            pad = _frozen;
            pad.Buttons |= _combination;
            if (_analogSeen)
            {
                pad.LeftX = _analog.LeftX;
                pad.LeftY = _analog.LeftY;
                pad.RightX = _analog.RightX;
                pad.RightY = _analog.RightY;
                pad.LeftTrigger = _analog.LeftTrigger;
                pad.RightTrigger = _analog.RightTrigger;
            }

            return true;
        }
    }

    /// <summary>Forgets the rebuilt pad, releasing any front key a combination report held; callers hold the state lock.</summary>
    private void EndSubstitution()
    {
        _substituting = false;
        _packetKnown = false;
        _releasedAt = 0;
        _combination = 0;
        _analogSeen = false;
        EmitCombinationFront(CanonicalButtons.None);
    }

    /// <summary>Publishes the front keys a combination report names as real press and release edges; callers hold the state lock.</summary>
    private void EmitCombinationFront(CanonicalButtons front)
    {
        var changed = front ^ _combinationFront;
        _combinationFront = front;
        if ((changed & CanonicalButtons.Guide) != 0)
        {
            Emit("guide", CanonicalButtons.Guide, (front & CanonicalButtons.Guide) != 0);
        }

        if ((changed & CanonicalButtons.QuickAccess) != 0)
        {
            Emit("qam", CanonicalButtons.QuickAccess, (front & CanonicalButtons.QuickAccess) != 0);
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
