using System.Numerics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

internal sealed class OemEventService(
    IMsiOemEventSource source,
    IDeviceSink host,
    OemButtonLatch oemButtons) : DeviceService<ClawIdentityState>(ServiceIds.OemEvents)
{
    /// <summary>
    ///     How close a WMI QS event and a firmware QS chord must be to count as one press. Where
    ///     MSI_Event is installed the firmware may send both for the same press.
    /// </summary>
    private static readonly TimeSpan SamePressWindow = TimeSpan.FromMilliseconds(500);

    private readonly IDeviceSink _host = host ?? throw new ArgumentNullException(nameof(host));

    private readonly OemButtonLatch _oemButtons =
        oemButtons ?? throw new ArgumentNullException(nameof(oemButtons));

    private readonly IMsiOemEventSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private long _lastQuickSettingsTicks;

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var started = await _source.StartAsync(PublishAsync, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The MSI_Event provider was unavailable."));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    /// <summary>
    ///     A QS press from the firmware's keyboard chord, called from the keyboard hook. Queued so the
    ///     hook returns at once.
    /// </summary>
    /// <param name="chord">Detected QS chord; QuickSettingsLong maps to a long press, other values to a short press.</param>
    public void RaiseChord(FirmwareChord chord)
    {
        var press = chord is FirmwareChord.QuickSettingsLong ? OemPressKind.Long : OemPressKind.Short;
        var timestamp = DateTimeOffset.UtcNow;
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => _ = state.Service.PublishPressAsync("oem2", state.Press, state.Timestamp, "chord"),
            (Service: this, Press: press, Timestamp: timestamp),
            false);
    }

    private ValueTask PublishAsync(byte code, DateTimeOffset timestamp)
    {
        // HC's keyMapping: 0x29 is OEM1 and 0x58 is OEM2. Every other code is ignored, as in HC.
        var controlId = code switch
        {
            0x29 => "oem1",
            0x58 => "oem2",
            _ => null
        };
        if (controlId is null)
        {
            return ValueTask.CompletedTask;
        }

        return PublishPressAsync(controlId, OemPressKind.Short, timestamp, $"msi-event-{code:X2}");
    }

    private ValueTask PublishPressAsync(string controlId, OemPressKind press, DateTimeOffset timestamp, string origin)
    {
        if (controlId == "oem2")
        {
            var previous = Interlocked.Exchange(ref _lastQuickSettingsTicks, timestamp.UtcTicks);
            if (timestamp.UtcTicks - previous < SamePressWindow.Ticks)
            {
                return ValueTask.CompletedTask;
            }
        }

        (string controlId, OemPressKind press)? mapped = (controlId, press);

        // The buttons the device is printed for: the left one is the virtual target's Steam button,
        // the right one its Quick Access button. Latched into the controller sample so Steam sees
        // its own controller press them, rather than WSGM acting on the user's behalf. A long press
        // on OEM2 is still only that button; the duration belongs to whatever reads it.
        var button = mapped.Value.controlId switch
        {
            "oem1" => CanonicalButtons.Guide,
            "oem2" => CanonicalButtons.QuickAccess,
            _ => CanonicalButtons.None
        };
        if (button != CanonicalButtons.None)
        {
            _oemButtons.Press(button, timestamp);
        }

        return _host.PublishOemEventAsync(
            new OemControlEvent(
                mapped.Value.controlId,
                mapped.Value.press,
                timestamp,
                $"{origin}-{timestamp.UtcTicks}"),
            CancellationToken.None);
    }
}

internal sealed class PowerService(
    ClawPowerCapability capability,
    DeviceRecoveryJournal journal) : ClawJournalledService(ServiceIds.Power)
{
    private readonly ClawPowerCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly DeviceRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    public override ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive, FirmwareReason(identity)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        return RestoreJournalledAsync(
            context,
            _journal,
            state => ClawRecoveryValues.TryPower(state, out var snapshot) ? snapshot : null,
            _capability.RestoreAsync,
            cancellationToken);
    }

    private static CapabilityReason FirmwareReason(ClawIdentityState identity)
    {
        return identity.ExactMachineMatch
            ? new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "The MSI_ACPI provider did not answer.")
            : new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                "The Claw model identity no longer matches.");
    }
}

internal sealed class ChargeLimitService : DeviceService<ClawIdentityState>
{
    internal ChargeLimitService() : base(ServiceIds.ChargeLimit)
    {
    }

    public override ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive, FirmwareReason(identity)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }

    private static CapabilityReason FirmwareReason(ClawIdentityState identity)
    {
        return identity.ExactMachineMatch
            ? new CapabilityReason(
                CapabilityReasonCode.FirmwareNotVerified,
                "The MSI_ACPI provider did not answer.")
            : new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                "The Claw model identity no longer matches.");
    }
}

internal sealed class FanService(
    ClawFanCapability capability,
    DeviceRecoveryJournal journal) : ClawJournalledService(ServiceIds.Fans)
{
    private readonly ClawFanCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    private readonly DeviceRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    public override ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive, new CapabilityReason(
                identity.ExactMachineMatch
                    ? CapabilityReasonCode.FirmwareNotVerified
                    : CapabilityReasonCode.Unsupported,
                "The Claw model or its MSI_ACPI provider was not available.")));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        return RestoreJournalledAsync(
            context,
            _journal,
            state => ClawRecoveryValues.TryFans(state, out var snapshot) ? snapshot : null,
            _capability.RestoreAsync,
            cancellationToken);
    }
}

internal sealed class TelemetryService(
    ClawFanCapability capability) : DeviceService<ClawIdentityState>(ServiceIds.Telemetry)
{
    private readonly ClawFanCapability _capability =
        capability ?? throw new ArgumentNullException(nameof(capability));

    public FanTelemetry? LastTelemetry { get; private set; }
    public DateTimeOffset? ObservedAt { get; private set; }

    internal override async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        LastTelemetry = await _capability.ReadTelemetryAsync(cancellationToken).ConfigureAwait(false);
        ObservedAt = DateTimeOffset.UtcNow;
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch || !identity.WmiAvailable)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "MSI telemetry requires the MSI_ACPI provider."));
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Owned);
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastTelemetry = null;
        ObservedAt = null;
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }
}

internal sealed class LightingService(
    IClawMcuTransport transport) : DeviceService<ClawIdentityState>(ServiceIds.Lighting)
{
    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        var identity = context.Identity;
        if (!identity.ExactMachineMatch)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                "Exact device identity no longer matches the Claw implementation."));
        }

        if (!await _transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The reviewed MCU HID collection was unavailable."));
        }

        return Set(DeviceServiceState.Owned);
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // RGB profile writes are intentionally persistent user choices. Deactivation never rewrites
        // the captured profile and therefore cannot silently undo the requested setting.
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }
}

internal sealed class MotionService(IClawMotionSource source, ClawModel model)
    : DeviceService<ClawIdentityState>(ServiceIds.Motion)
{
    /// <summary>How long a sensor reading may still be attached to a controller sample.</summary>
    /// <remarks>
    ///     The physical gyrometer is configured for a 10 ms report interval while the controller reader
    ///     runs at about 125 Hz, so the same reading legitimately rides an adjacent frame. Five sensor
    ///     periods cover ordinary scheduler jitter without replaying a non-zero value after the device
    ///     or its Intel transport stops producing fresh hardware reports.
    /// </remarks>
    internal static readonly TimeSpan MaximumMotionAge = TimeSpan.FromMilliseconds(50);

    /// <summary>How long staleness must persist before it is worth a line.</summary>
    /// <remarks>
    ///     Crossing <see cref="MaximumMotionAge" /> is not news: measured Intel transport jitter puts a
    ///     dense cluster of readings at 51-59 ms, just past the cap, so reporting each crossing produced
    ///     two alternating lines about 1.3 times a second: 7,619 lines and 40% of one day's log. The
    ///     decay those crossings cause is a couple of milliseconds inside a 52 ms interval and is not
    ///     what anyone is being told about. A pause worth reading about outlasts the jitter by an order
    ///     of magnitude.
    /// </remarks>
    internal static readonly TimeSpan StaleReportDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock _latestGate = new();
    private readonly GyroFrameResampler _resampler = new(MaximumMotionAge);
    private readonly IClawMotionSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private int _cycle;
    private MotionSample? _latest;
    private int _staleReported;

    public override bool Suspendable => true;

    /// <summary>The motion to attach to the controller sample being published now.</summary>
    /// <param name="now">Current time, from the caller's clock.</param>
    /// <returns>
    ///     The last reading with its angular velocity replaced by the frame-average since the previous
    ///     call, or null before the first reading arrives.
    /// </returns>
    /// <remarks>
    ///     The sensor updates at 100 Hz under a ~125 Hz controller reader, so raw values ride frames
    ///     unevenly in a repeating beat that Steam integrates as jagged angular steps. The resampled
    ///     average preserves the exact integrated angle per frame and decays to zero when the sensor
    ///     goes quiet, which is also what keeps a still device from ever reading as freefall.
    /// </remarks>
    public MotionSample? Current(DateTimeOffset now)
    {
        MotionSample sample;
        Vector3 average;
        var stopped = false;
        var resumed = false;
        lock (_latestGate)
        {
            if (_latest is not { } latest)
            {
                return null;
            }

            sample = latest;
            if (sample.SensorTimestamp is not { } stamp)
            {
                return sample;
            }

            var age = now - stamp;
            if (age >= StaleReportDelay && _staleReported == 0)
            {
                _staleReported = 1;
                stopped = true;
            }
            else if (age >= TimeSpan.Zero && age <= MaximumMotionAge && _staleReported != 0)
            {
                _staleReported = 0;
                resumed = true;
            }

            average = _resampler.FrameAverage(now);
        }

        // Crossing MaximumMotionAge is not itself news. That threshold governs the resampler's quiet
        // cap and only a current measurement can end a reported pause. The held rest is correct: the average
        // decays to zero while the last measured acceleration keeps Steam's fusion anchored, so an
        // Intel transport pause becomes neither continuous rotation nor freefall. Only a pause that
        // outlasts ordinary jitter tells a reader something. This runs on the ~125 Hz controller
        // reader, so it reports once per stretch at most.
        if (stopped)
        {
            DeviceTrace.Change(
                "motion",
                "freshness",
                $"Gyroscope reports stopped for over {StaleReportDelay.TotalMilliseconds:F0} ms; "
                + "holding rest (decayed angular velocity, last measured acceleration) until they resume.");
        }

        if (resumed)
        {
            DeviceTrace.Change("motion", "freshness", "Gyroscope reports resumed.");
        }

        return sample with
        {
            GyroX = average.X,
            GyroY = average.Y,
            GyroZ = average.Z
        };
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        int cycle;
        lock (_latestGate)
        {
            _latest = null;
            _staleReported = 0;
            cycle = ++_cycle;
            _resampler.Reset();
        }

        var started = await _source.StartAsync(
            model,
            sample =>
            {
                lock (_latestGate)
                {
                    if (_cycle != cycle)
                    {
                        return;
                    }

                    if (sample.SensorTimestamp is { } stamp && !_resampler.OnReading(
                            new Vector3(sample.GyroX, sample.GyroY, sample.GyroZ), stamp))
                    {
                        return;
                    }

                    _latest = sample;
                }
            },
            cancellationToken).ConfigureAwait(false);
        return started
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Intel ISS physical gyrometer or accelerometer was unavailable; no synthetic fallback exists."));
    }

    public override ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        lock (_latestGate)
        {
            // Retire the publication owner before a bounded native stop waits for callbacks.
            _cycle++;
        }

        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_latestGate)
        {
            _latest = null;
            _resampler.Reset();
        }

        return Set(DeviceServiceState.Idle);
    }
}

internal sealed class ChordSuppressorService(
    IFirmwareChordSuppressor suppressor,
    OemEventService oemEvents,
    IDeviceSink host) : DeviceService<ClawIdentityState>(ServiceIds.ChordSuppressor)
{
    private readonly IDeviceSink _host = host ?? throw new ArgumentNullException(nameof(host));

    private readonly OemEventService _oemEvents = oemEvents
                                                  ?? throw new ArgumentNullException(nameof(oemEvents));

    private readonly IFirmwareChordSuppressor _suppressor = suppressor
                                                            ?? throw new ArgumentNullException(nameof(suppressor));

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        // HC's chords work without MSI_Event, and are the only path to QS where it is missing.
        var started = await _suppressor.StartAsync(ReportFault, _oemEvents.RaiseChord, cancellationToken)
            .ConfigureAwait(false);
        return started
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                "The bounded low-level keyboard hook could not be installed."));
    }

    /// <summary>The keyboard hook stopped: the chords are gone until the next start, nothing else is.</summary>
    private void ReportFault(Exception exception)
    {
        var detail = DiagnosticText.FromException("The firmware chord suppressor stopped", exception);
        DeviceTrace.Write(DeviceTraceLevel.Warn, "chords", detail);
        _ = Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    public override ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        return ReleaseAsync(context, cancellationToken);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await _suppressor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }
}
