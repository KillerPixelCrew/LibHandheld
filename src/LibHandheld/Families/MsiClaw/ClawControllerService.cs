using LibHandheld.Contracts;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

internal sealed class ControllerService(
    IClawMcuTransport mcu,
    IClawControllerSource source,
    MotionService motion,
    IDeviceSink host,
    DeviceRecoveryJournal journal,
    ClawModel model,
    Func<TimeSpan, CancellationToken, Task> delay) : DeviceService<ClawIdentityState>(ServiceIds.Controller)
{
    /// <summary>
    ///     The mode the controller goes back to on release: XInput, as HC's <c>Close</c> and app exit
    ///     both switch it, whatever mode it was found in.
    /// </summary>
    private const ClawControllerMode ReleaseMode = ClawControllerMode.XInput;

    /// <summary>
    ///     What the Claw's MCU can actually do with output.
    /// </summary>
    /// <remarks>
    ///     Two motors and no trigger haptics: the rumble report
    ///     (<see cref="ClawControllerCodec.EncodeRumble" />) carries one weak and one strong byte and
    ///     nothing else. WSGM's output router paces frames to the declared rate and never holds a stop
    ///     back, so this service writes every frame it is given.
    /// </remarks>
    private static readonly HapticCapabilities OutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 250,
        // ERM motors: LRA-grade haptic ticks must be floored and stretched by the host to be
        // perceptible at all. Device-measured with the attended A-button sweep (2026-09-02):
        // 30 ms ticks are felt down to 56/255 and vanish at 48; full-strength pulses stay
        // reliable to about 10 ms (below that the sleep granularity dominates); continuous
        // rumble is felt down to 24/255, which is why the floor applies to bounded events only.
        MinimumStartIntensity = 56f / 255f,
        MinimumPulse = TimeSpan.FromMilliseconds(10)
    };

    /// <summary>
    ///     The Claw A1M's output as HC drives it: each motor on at one level or off, sampled every
    ///     100 ms by <c>DClawController</c>'s rumble thread. Any nonzero intensity is full, so there is
    ///     no start floor, and a pulse shorter than one sample would be lost.
    /// </summary>
    private static readonly HapticCapabilities BinaryOutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 10,
        MinimumPulse = ClawModels.BinaryRumbleInterval
    };

    /// <summary>HC's <c>GetM12</c> DirectInput mapping payload after the address: map the paddle as a button.</summary>
    private static readonly byte[] PaddleDirectInputMapping = [0x01, 0x00];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? throw new ArgumentNullException(nameof(delay));

    private readonly IDeviceSink _host = host ?? throw new ArgumentNullException(nameof(host));
    private readonly DeviceRecoveryJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IClawMcuTransport _mcu = mcu ?? throw new ArgumentNullException(nameof(mcu));
    private readonly MotionService _motion = motion ?? throw new ArgumentNullException(nameof(motion));
    private readonly SemaphoreSlim _outputSerializer = new(1, 1);
    private readonly DeviceReconnect _reconnect = new();
    private readonly IClawControllerSource _source = source ?? throw new ArgumentNullException(nameof(source));
    private DeviceCycleContext<ClawIdentityState>? _context;
    private byte _lastStrong;
    private byte _lastWeak;
    private ControllerTopology? _original;
    private bool _paddlesWritten;
    private CanonicalButtons _rearButtons;

    public bool Enabled { get; set; }

    private ControllerTopology? CurrentTopology { get; set; }

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.ResourceReleased,
                "Controller management is disabled."));
        }

        _context = context;


        // Exact machine identity is the only gate. The MCU revision is deliberately not one: the
        // mode switch and the hide list are not addressed writes, and gating them on the revision
        // took the controller away from every unit MSI updated to 0230.
        var identity = context.Identity;
        if (!identity.ExactMachineMatch)
        {
            DeviceTrace.Write(
                DeviceTraceLevel.Warn,
                "controller",
                "acquire refused at the identity gate: the Claw model identity no longer matches.");
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.Unsupported,
                "Exact device identity no longer matches the Claw implementation."));
        }

        var observed = await _source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (observed is null)
        {
            // After a wake the pad comes back a few seconds late. Nothing is final here: it is taken as
            // soon as it is there, as HC takes it on Device_Inserted. Giving up on the first look left
            // the controller dead after a wake.
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller", "no Claw controller yet; waiting for it.");
            _reconnect.Start(ReacquireAsync, OnReacquireFailed);
            return Set(DeviceServiceState.Degraded, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The Claw controller is not present yet."));
        }

        // Traced before the gates below, which would reduce the mode, product and endpoints to one
        // sentence about a prerequisite.
        DeviceTrace.Write(
            DeviceTraceLevel.Info,
            "controller",
            $"discovered mode={observed.Mode}, product=0x{observed.ProductId:X4}, "
            + $"location='{observed.PhysicalLocation}', "
            + $"physicalDevices={observed.PhysicalDevices.Count}, "
            + $"endpoints=[{observed.ObservedEndpoints}]");
        _original ??= observed;
        if (string.IsNullOrWhiteSpace(_original.PhysicalLocation)
            || !HidDevices.SamePhysicalLocation(observed.PhysicalLocation, _original.PhysicalLocation))
        {
            DeviceTrace.Write(
                DeviceTraceLevel.Warn,
                "controller",
                "acquire refused: composite USB location did not match the one first observed. "
                + $"original='{_original.PhysicalLocation}', "
                + $"observed='{observed.PhysicalLocation}'.");
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                "The physical controller or its composite USB location was unavailable."));
        }

        CurrentTopology = observed;

        // HC's Open and every mode change write the M1/M2 DirectInput mapping and commit it to ROM
        // before switching, so the paddles report as DirectInput buttons 15 and 16 whatever MSI
        // Center stored for them.
        await ConfigurePaddlesAsync(context, cancellationToken).ConfigureAwait(false);
        if (CurrentTopology.Mode is not ClawControllerMode.DirectInput)
        {
            DeviceWriteBudget.Require(context.Deadline, "controller mode acquisition");
            _ = await _journal.BeginAsync(
                ServiceId,
                ClawFirmwareIdentities.Mcu,
                ClawRecoveryValues.ControllerMode(ReleaseMode),
                cancellationToken).ConfigureAwait(false);
            DeviceTrace.Write(
                DeviceTraceLevel.Info,
                "controller",
                $"switching MCU mode {CurrentTopology.Mode} -> DirectInput at '{_original.PhysicalLocation}'.");
            try
            {
                CurrentTopology = await _mcu.SwitchModeAsync(
                    ClawControllerMode.DirectInput,
                    _original.PhysicalLocation,
                    context.Deadline,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DeviceTrace.Write(
                    DeviceTraceLevel.Error,
                    "controller",
                    $"mode switch to DirectInput failed: {ex.GetType().Name}: {ex.Message}");
                await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
                throw;
            }

            // The switch reports success by returning a topology, but the topology is what the
            // hardware actually settled into. Those differed on the reference unit, and nothing
            // said so.
            DeviceTrace.Write(
                CurrentTopology.Mode is ClawControllerMode.DirectInput
                    ? DeviceTraceLevel.Info
                    : DeviceTraceLevel.Warn,
                "controller",
                $"mode switch settled at {CurrentTopology.Mode}, product=0x{CurrentTopology.ProductId:X4}, "
                + $"physicalDevices={CurrentTopology.PhysicalDevices.Count}, "
                + $"endpoints=[{CurrentTopology.ObservedEndpoints}]");
        }
        else
        {
            DeviceTrace.Write(
                DeviceTraceLevel.Info,
                "controller",
                "controller already in DirectInput; no mode switch needed.");
        }

        if (CurrentTopology.PhysicalDevices.Count == 0)
        {
            // Captured before the restore, which clears _current: the whole point of this reason is
            // to say what was observed, and reading it afterwards is reading nothing.
            var detail = "No exact DirectInput physical interface identity was available for "
                         + $"handoff. Mode={CurrentTopology.Mode}, product=0x{CurrentTopology.ProductId:X4}, "
                         + $"endpoints=[{CurrentTopology.ObservedEndpoints}]";
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller", detail);
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            return Set(DeviceServiceState.Passive, new CapabilityReason(
                CapabilityReasonCode.PrerequisiteMissing,
                detail));
        }

        // The XInput pad the switch replaced is hidden too, as HC hides the controller it finds before
        // switching. The firmware falls back to XInput in every sleep, and that pad reappeared on the wake
        // with nothing hiding it, so Steam saw an Xbox controller until the switch settled.
        IReadOnlyList<PhysicalDeviceIdentity> hidden =
        [
            .. observed.PhysicalDevices
                .Concat(CurrentTopology.PhysicalDevices)
                .DistinctBy(device => device.InstancePath, StringComparer.OrdinalIgnoreCase)
        ];
        try
        {
            await _source.StartAsync(
                model,
                PublishControllerSampleAsync,
                ReportControllerReaderFault,
                cancellationToken).ConfigureAwait(false);

            await _host.PublishPhysicalDevicesAsync(
                hidden,
                model.BinaryRumble ? BinaryOutputCapabilities : OutputCapabilities,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DeviceTrace.Write(
                DeviceTraceLevel.Error,
                "controller",
                $"controller acquisition failed before physical-device handoff completed: "
                + $"{ex.GetType().Name}: {ex.Message}");
            await RestoreAfterFailedAcquireAsync(context.Deadline).ConfigureAwait(false);
            throw;
        }

        DeviceTrace.Write(
            DeviceTraceLevel.Info,
            "controller",
            $"owned: published {hidden.Count} physical identities for hiding, "
            + "haptics=True.");

        // Haptics write nothing until the service is Owned, so this is the one place the last levels can
        // be cleared without racing a write: the first frame of the new ownership is always written.
        await _outputSerializer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _lastWeak = 0;
        _lastStrong = 0;
        _outputSerializer.Release();
        return Set(DeviceServiceState.Owned);
    }

    /// <summary>The DirectInput collection stopped reading: the pad went away, which is never a device fault.</summary>
    /// <remarks>
    ///     HC's read loop treats any failure as <c>Device_Removed</c> and takes the pad again on
    ///     <c>Device_Inserted</c>. Faulting here took fans, TDP and the OEM buttons down with the pad, for
    ///     a drop that every sleep produces.
    /// </remarks>
    private void ReportControllerReaderFault(Exception exception)
    {
        if (State is not DeviceServiceState.Owned)
        {
            return;
        }

        var detail = DiagnosticText.FromException("The controller reader stopped", exception);
        DeviceTrace.Write(DeviceTraceLevel.Warn, "controller", detail + "; waiting for the pad to come back.");
        _ = Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        if (_context is { } context)
        {
            _ = NeutralizeReaderAndReconnectAsync(context);
        }
    }

    private async Task NeutralizeReaderAndReconnectAsync(DeviceCycleContext<ClawIdentityState> context)
    {
        try
        {
            if (_context == context && State is DeviceServiceState.Degraded)
            {
                _rearButtons = CanonicalButtons.None;
                await _host.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("controller", "Reader-loss neutral publication failed", ex);
        }
        finally
        {
            if (_context == context && State is DeviceServiceState.Degraded)
            {
                _reconnect.Start(ReacquireAsync, OnReacquireFailed);
            }
        }
    }

    /// <summary>Takes the pad again once it answers, the way HC's <c>Device_Inserted</c> reopens it.</summary>
    /// <returns>False only while the pad is still absent, which is the only result that wrote nothing.</returns>
    /// <remarks>
    ///     The paddle commit and the mode switch run once for each return of the pad. A result other than
    ///     Owned stays as it is until the user turns controller management on again or the next cycle.
    /// </remarks>
    private async ValueTask<bool> ReacquireAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _context is not { } context)
        {
            return true;
        }

        await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        var result = await AcquireAsync(context with { Deadline = Deadline.After(TimeSpan.FromSeconds(12)) },
            cancellationToken).ConfigureAwait(false);
        if (result.State is DeviceServiceState.Owned)
        {
            DeviceTrace.Write(DeviceTraceLevel.Info, "controller", "the pad is back.");
        }

        return result.State is not DeviceServiceState.Degraded;
    }

    /// <summary>
    ///     Taking the pad again threw. Its writes are not repeated: the service faults until a user action.
    /// </summary>
    private void OnReacquireFailed(Exception exception)
    {
        var detail = DiagnosticText.FromException("Taking the pad again failed", exception);
        DeviceTrace.Write(DeviceTraceLevel.Error, "controller",
            detail + "; turn controller management off and on to try again.");
        _ = Set(DeviceServiceState.Faulted, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    public override async ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await StopOutputAndAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    /// <summary>Stops reading and puts the controller back in XInput mode, as HC's <c>Close</c> does.</summary>
    /// <param name="deadline">Bounds the mode switch.</param>
    /// <param name="cancellationToken">Cancels waiting for the reader and the switch.</param>
    /// <remarks>
    ///     Best effort: every step runs, a failed one is traced and recorded in the recovery journal for
    ///     the next start, and the service ends idle. Nothing here is a reason to keep the device down.
    /// </remarks>
    /// <returns>Completion of the bounded release attempt; does not guarantee mode restoration or controller readback.</returns>
    public async ValueTask ReleaseControllerAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        _ = Set(DeviceServiceState.Releasing);
        await StopOutputAndAcquisitionAsync(cancellationToken).ConfigureAwait(false);
        if (_original is null || CurrentTopology is null)
        {
            CurrentTopology = null;
            if (_journal.HasUnrestoredMutation(ServiceId))
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                    "the controller disappeared before its original mode could be put back.");
                await _journal.SetStatusAsync(
                    ServiceId,
                    DeviceRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
            }

            _ = Set(DeviceServiceState.Idle);
            return;
        }

        DeviceRecoveryStatus? status = DeviceRecoveryStatus.RestoredVerified;
        if (CurrentTopology.Mode != ReleaseMode && !DeviceWriteBudget.IsAvailable(deadline))
        {
            // Nothing is written and the entry is left as it is: the next start reads the mode and
            // puts it back.
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                "too little time to put the original controller mode back; the next start restores it.");
            status = null;
        }
        else if (CurrentTopology.Mode != ReleaseMode)
        {
            try
            {
                var restored = await _mcu.SwitchModeAsync(
                    ReleaseMode,
                    _original.PhysicalLocation,
                    deadline,
                    cancellationToken).ConfigureAwait(false);
                if (restored.Mode != ReleaseMode
                    || !HidDevices.SamePhysicalLocation(restored.PhysicalLocation, _original.PhysicalLocation))
                {
                    DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                        $"mode switch back settled at {restored.Mode} at '{restored.PhysicalLocation}'.");
                    status = DeviceRecoveryStatus.RestoredUnverified;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                    $"the original controller mode could not be put back: {ex.GetType().Name}: {ex.Message}");
                status = DeviceRecoveryStatus.RestoreFailed;
            }
        }

        CurrentTopology = null;
        _rearButtons = CanonicalButtons.None;
        if (status is { } recorded)
        {
            await _journal.SetStatusAsync(ServiceId, recorded, CancellationToken.None)
                .ConfigureAwait(false);
        }

        _ = Set(DeviceServiceState.Idle);
    }

    public async ValueTask ApplyHapticsAsync(
        HapticOutputFrame frame,
        CancellationToken cancellationToken)
    {
        await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not DeviceServiceState.Owned)
            {
                return;
            }

            var weak = ToByte(frame.HighFrequency);
            var strong = ToByte(frame.LowFrequency);
            if (model.BinaryRumble)
            {
                // HC's DClawController drives the Claw A1M's motors on or off at one level.
                weak = weak == 0 ? (byte)0 : ClawModels.BinaryRumbleLevel;
                strong = strong == 0 ? (byte)0 : ClawModels.BinaryRumbleLevel;
            }

            if (weak == _lastWeak && strong == _lastStrong)
            {
                return;
            }

            await _source.WriteRumbleAsync(weak, strong, cancellationToken).ConfigureAwait(false);
            _lastWeak = weak;
            _lastStrong = strong;
        }
        finally
        {
            _outputSerializer.Release();
        }
    }

    /// <summary>HC's <c>ApplyM12Configuration</c>: M1, M2, then <c>SyncToROM</c>, with its sleeps.</summary>
    private async ValueTask ConfigurePaddlesAsync(
        DeviceCycleContext<ClawIdentityState> context,
        CancellationToken cancellationToken)
    {
        // The mapping is synced to ROM and survives sleep (Claw 8 AI+, 2026-10-20), so a wake does not
        // write it again as HC's Device_Inserted does: that rewrite cost 2.4 s of every pad restore.
        if (_paddlesWritten)
        {
            return;
        }

        var layout = ClawModels.McuLayout(context.Identity.Snapshot.McuFirmwareVersion);

        // HC's sleeps add almost two seconds; the mode switch after them needs its own budget.
        if (context.Deadline.Remaining < TimeSpan.FromSeconds(6))
        {
            DeviceTrace.Write(DeviceTraceLevel.Info, "controller",
                "paddle mapping skipped: the acquisition deadline leaves no room for HC's write spacing.");
            return;
        }

        try
        {
            await _delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);
            await _mcu.WriteProfileAsync(layout.M1DirectInput, PaddleDirectInputMapping, cancellationToken)
                .ConfigureAwait(false);
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            await _mcu.WriteProfileAsync(layout.M2DirectInput, PaddleDirectInputMapping, cancellationToken)
                .ConfigureAwait(false);
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            await _mcu.SyncToRomAsync(cancellationToken).ConfigureAwait(false);
            await _delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            _paddlesWritten = true;
            DeviceTrace.Write(DeviceTraceLevel.Info, "controller",
                $"paddle mapping written at 0x{layout.M1DirectInput:X4}/0x{layout.M2DirectInput:X4} and synced to ROM.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // HC does not check these writes either; the controller is still taken.
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                $"paddle mapping write failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async ValueTask StopOutputAndAcquisitionAsync(
        CancellationToken cancellationToken)
    {
        await _reconnect.StopAsync().ConfigureAwait(false);
        var ownsOutput = false;
        try
        {
            await _outputSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsOutput = true;
            try
            {
                await _source.WriteRumbleAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The device may already be gone; acquisition cleanup and mode restoration continue.
                DeviceTrace.Write(
                    DeviceTraceLevel.Warn,
                    "controller",
                    $"zero-rumble write during release failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // StopAsync below closes the stream to abort the in-flight output at the lifecycle deadline.
        }
        finally
        {
            if (ownsOutput)
            {
                _outputSerializer.Release();
            }
        }

        try
        {
            await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The physical source may have vanished. Continue to the mode restoration, which is the
            // cleanup step that keeps external input usable.
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                $"the controller source did not stop cleanly: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        var current = sample.Buttons
                      & (CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);
        var changed = current ^ _rearButtons;
        if ((changed & CanonicalButtons.RearPaddle1) != 0)
        {
            await PublishRearEventAsync(
                "oem3",
                (current & CanonicalButtons.RearPaddle1) != 0,
                sample,
                cancellationToken).ConfigureAwait(false);
        }

        if ((changed & CanonicalButtons.RearPaddle2) != 0)
        {
            await PublishRearEventAsync(
                "oem4",
                (current & CanonicalButtons.RearPaddle2) != 0,
                sample,
                cancellationToken).ConfigureAwait(false);
        }

        _rearButtons = current;
        // Aged rather than taken blindly. If the physical sensor counter stops advancing while the
        // controller keeps reporting, the last reading must not replay a non-zero angular velocity
        // through the virtual Deck indefinitely.
        await _host.PublishControllerSampleAsync(
            sample with { Motion = _motion.Current(sample.Timestamp) },
            cancellationToken).ConfigureAwait(false);
    }

    private ValueTask PublishRearEventAsync(
        string controlId,
        bool pressed,
        CanonicalControllerSample sample,
        CancellationToken cancellationToken)
    {
        return _host.PublishOemEventAsync(
            new OemControlEvent(
                controlId,
                OemPressKind.Short,
                sample.Timestamp,
                $"claw-hid-{controlId}-{sample.Timestamp.UtcTicks}",
                pressed ? OemControlEdge.Pressed : OemControlEdge.Released),
            cancellationToken);
    }

    private async ValueTask RestoreAfterFailedAcquireAsync(Deadline deadline)
    {
        try
        {
            var observed = await _source.DiscoverAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (observed is null)
            {
                CurrentTopology = null;
                CapabilityReason reason = new(
                    CapabilityReasonCode.TransportFaulted,
                    "Controller topology vanished during acquisition rollback.");
                Fault(reason);
                await _journal.SetStatusAsync(
                    ServiceId,
                    DeviceRecoveryStatus.RestoreFailed,
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            CurrentTopology = observed;
            await ReleaseControllerAsync(deadline, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            CapabilityReason reason = new(
                CapabilityReasonCode.TransportFaulted,
                "Controller acquisition rollback failed.");
            Fault(reason);
            await _journal.SetStatusAsync(
                ServiceId,
                DeviceRecoveryStatus.RestoreFailed,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static byte ToByte(float value)
    {
        return checked((byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue));
    }
}
