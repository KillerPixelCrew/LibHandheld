using System.ComponentModel;
using System.Numerics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>The IMU, attached to controller samples with the Claw's frame resampler.</summary>
/// <param name="source">Shared motion source whose stream is started and stopped for each service cycle.</param>
internal sealed class AllyMotionService(IAllyMotionSource source)
    : DeviceService<AllyIdentityState>(AllyServiceIds.Motion)
{
    /// <summary>How long a reading may still ride a controller sample. The Claw's value.</summary>
    internal static readonly TimeSpan MaximumMotionAge = TimeSpan.FromMilliseconds(50);

    private readonly Lock _gate = new();
    private readonly GyroFrameResampler _resampler = new(MaximumMotionAge);
    private MotionSample? _latest;

    public override bool Suspendable => true;

    public MotionSample? Current(DateTimeOffset now)
    {
        MotionSample sample;
        lock (_gate)
        {
            if (_latest is not { } latest)
            {
                return null;
            }

            sample = latest;
        }

        if (sample.SensorTimestamp is null)
        {
            return sample;
        }

        var average = _resampler.FrameAverage(now);
        return sample with { GyroX = average.X, GyroY = average.Y, GyroZ = average.Z };
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _latest = null;
        }

        _resampler.Reset();
        var started = await source.StartAsync(sample =>
        {
            lock (_gate)
            {
                _latest = sample;
            }

            if (sample.SensorTimestamp is { } stamp)
            {
                _resampler.OnReading(new Vector3(sample.GyroX, sample.GyroY, sample.GyroZ), stamp);
            }
        }, cancellationToken).ConfigureAwait(false);
        return started
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, Missing("No Windows gyrometer and accelerometer pair was found."));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        await source.StopAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _latest = null;
        }

        _resampler.Reset();
        return Set(DeviceServiceState.Idle);
    }
}

/// <summary>The physical pad: controller tables, sample stream, haptics and the HidHide identities.</summary>
/// <param name="source">Physical XInput discovery, sampling and rumble transport.</param>
/// <param name="vendor">Shared vendor HID transport for controller configuration tables.</param>
/// <param name="motion">Motion service sampled alongside each controller frame.</param>
/// <param name="keyboard">Keyboard service controlling the rear-key watch and mapping.</param>
/// <param name="buttons">Shared OEM-button holds and latches merged into controller samples.</param>
/// <param name="host">Host callbacks for controller delivery, physical-device hiding and diagnostics.</param>
/// <param name="journal">Durable restore obligation for the MCU controller tables.</param>
internal sealed class ControllerService(
    IAllyControllerSource source,
    IAllyVendorHid vendor,
    AllyMotionService motion,
    KeyboardOemService keyboard,
    AllyOemButtonState buttons,
    IDeviceSink host,
    DeviceRecoveryJournal journal) : DeviceService<AllyIdentityState>(AllyServiceIds.Controller)
{
    /// <summary>What the Ally's motors can do: two rumble motors, no trigger haptics.</summary>
    /// <remarks>
    ///     XInput carries one large and one small motor. The frame rate matches the pad poll. Motor
    ///     floors are not declared: nothing has measured them on an Ally.
    /// </remarks>
    internal static readonly HapticCapabilities OutputCapabilities = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native,
        LeftTrigger = OutputChannelSupport.Unsupported,
        RightTrigger = OutputChannelSupport.Unsupported,
        MaxFramesPerSecond = 125
    };

    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly DeviceReconnect _reconnect = new();
    private bool _configured;
    private DeviceCycleContext<AllyIdentityState>? _context;
    private float _lastHigh;
    private float _lastLow;
    private AllyControllerTopology? _topology;

    public bool Enabled { get; set; }

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return Set(DeviceServiceState.Passive, new CapabilityReason(CapabilityReasonCode.ResourceReleased,
                "Controller management is disabled."));
        }


        if (!context.Identity.ExactMachineMatch)
        {
            return Set(DeviceServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        if (State is DeviceServiceState.Owned && _topology is not null)
        {
            // Already reading the pad. Samples carry no cycle, so nothing restarts.
            _context = context;
            return Set(DeviceServiceState.Owned);
        }

        if (_topology is not null || _configured)
        {
            // A reader that faulted left its topology and tables behind; release them before starting over.
            await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        }

        _context = context;
        var topology = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (topology is null)
        {
            // After a wake the XInput slot and the pad's device nodes come back a few seconds late, and
            // not together. Nothing is final here: the pad is taken as soon as both are there, as HC takes
            // it on arrival. Giving up on the first look left the controller dead after a wake.
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                "the ASUS XInput pad and its device nodes are not both present yet; waiting for them.");
            _reconnect.Start(AttachWhenBackAsync, OnReconnectFailed);
            return Set(DeviceServiceState.Degraded, Missing("The Ally gamepad is not present yet."));
        }

        DeviceTrace.Write(DeviceTraceLevel.Info, "controller",
            $"devices={topology.PhysicalDevices.Count}, observed=[{topology.Observed}]");
        _topology = topology;
        await ConfigureAsync(context, cancellationToken).ConfigureAwait(false);
        try
        {
            await StartReadingAsync(topology, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DeviceTrace.Write(DeviceTraceLevel.Error, "controller",
                DiagnosticText.FromException("controller acquisition failed", ex));
            await ReleaseControllerAsync(context.Deadline, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return Set(DeviceServiceState.Owned);
    }

    public override async ValueTask<DeviceServiceResult> SuspendAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        await ReleaseControllerAsync(context.Deadline, cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    /// <summary>Stops the motors and the reader and puts the firmware's tables back.</summary>
    /// <param name="deadline">Bounds the table writes.</param>
    /// <param name="cancellationToken">Cancels waiting for the reader.</param>
    /// <remarks>
    ///     Best effort, like HC's <c>Close</c>: every step runs, a failed one is traced, and the service
    ///     ends idle. The zero-rumble write fails whenever the pad has already dropped off the bus, which
    ///     is exactly when a release happens, so it cannot be a reason to report anything.
    /// </remarks>
    /// <returns>
    ///     Completion after best-effort cleanup; ordinary motor, reader and table failures are traced and the service
    ///     ends idle.
    /// </returns>
    public async ValueTask ReleaseControllerAsync(
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        await _reconnect.StopAsync().ConfigureAwait(false);
        if (_topology is null && !_configured)
        {
            // Never acquired, or already released: there is no reader, motor or table to put back.
            _ = Set(DeviceServiceState.Idle);
            return;
        }

        _ = Set(DeviceServiceState.Releasing);
        await _outputGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await source.WriteRumbleAsync(0, 0, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Write(DeviceTraceLevel.Debug, "controller",
                DiagnosticText.FromException("zero rumble failed", ex));
        }
        finally
        {
            _outputGate.Release();
        }

        try
        {
            await source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                DiagnosticText.FromException("The controller reader did not stop cleanly", ex));
        }

        await keyboard.SetRearEnabledAsync(false, false, CancellationToken.None).ConfigureAwait(false);
        buttons.Release(CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2);
        _lastLow = 0;
        _lastHigh = 0;

        if (_configured && await RestoreConfigurationAsync(deadline).ConfigureAwait(false) is { } restoreFailure)
        {
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                restoreFailure.Detail ?? "The firmware's controller tables were not all written back.");
        }

        _topology = null;
        _ = Set(DeviceServiceState.Idle);
    }

    /// <remarks>The host already clamped the frame to <see cref="OutputCapabilities" />.</remarks>
    /// <summary>Serializes rumble updates, suppressing small repeated changes while always admitting a transition to zero.</summary>
    /// <param name="frame">Host-clamped low/high motor frame; unsupported trigger channels are not written.</param>
    /// <param name="cancellationToken">Cancels waiting for the output gate or the transport operation.</param>
    /// <returns>
    ///     Completion after an admitted motor write, or immediately when not owned or the change is suppressed; transport
    ///     failures propagate.
    /// </returns>
    public async ValueTask ApplyHapticsAsync(HapticOutputFrame frame, CancellationToken cancellationToken)
    {
        await _outputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is not DeviceServiceState.Owned)
            {
                return;
            }

            if (!((frame.LowFrequency == 0 && _lastLow != 0) || (frame.HighFrequency == 0 && _lastHigh != 0))
                && Math.Abs(frame.LowFrequency - _lastLow) < 0.002f
                && Math.Abs(frame.HighFrequency - _lastHigh) < 0.002f)
            {
                return;
            }

            await source.WriteRumbleAsync(frame.LowFrequency, frame.HighFrequency, cancellationToken)
                .ConfigureAwait(false);
            _lastLow = frame.LowFrequency;
            _lastHigh = frame.HighFrequency;
        }
        finally
        {
            _outputGate.Release();
        }
    }

    /// <summary>Writes the tables that turn M1/M2 into F17/F18, journalled on the first write of the cycle.</summary>
    /// <remarks>
    ///     HC writes every table and ignores each result (<c>ROGAlly.cs:646-668</c>), on open and on every
    ///     <c>Device_Inserted</c>. Without the M1/M2 table the rear keys still work on a model whose firmware
    ///     sends them natively; they keep the firmware's own sides.
    /// </remarks>
    private async ValueTask ConfigureAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        var rearWritten = false;
        if (!await vendor.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                "no vendor collection for the controller tables; M1 and M2 stay unavailable.");
        }
        else if (!DeviceWriteBudget.IsAvailable(context.Deadline))
        {
            DeviceTrace.Write(DeviceTraceLevel.Warn, "controller",
                "too little time to write the controller tables; M1 and M2 stay unavailable this cycle.");
        }
        else
        {
            if (!_configured)
            {
                // An entry a refused release left pending keeps it; the factory tables are the original anyway.
                _ = await journal.BeginAsync(ServiceId, AllyServiceIds.McuFirmware, AllyRecoveryState.Controller(),
                    cancellationToken).ConfigureAwait(false);
                _configured = true;
            }

            var refused = await WriteTablesAsync(AllyProtocol.GameModeConfiguration, cancellationToken)
                .ConfigureAwait(false);
            rearWritten = !refused.Contains(AllyProtocol.RearKeyboardMapping);
            DeviceTrace.Write(rearWritten ? DeviceTraceLevel.Info : DeviceTraceLevel.Error, "controller", rearWritten
                ? "controller tables written; left rear sends F17, right rear F18."
                : "the M1/M2 table was not written.");
        }

        if (rearWritten)
        {
            await keyboard.SetRearEnabledAsync(true, true, cancellationToken).ConfigureAwait(false);
        }
        else if (context.Identity.Model?.RearKeysNative == true)
        {
            await keyboard.SetRearEnabledAsync(true, false, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends every table even when one is refused, as HC does; each is one write, never retried.</summary>
    /// <returns>The tables the MCU refused, each already traced.</returns>
    private async ValueTask<IReadOnlyList<byte[]>> WriteTablesAsync(
        IReadOnlyList<byte[]> reports,
        CancellationToken cancellationToken)
    {
        List<byte[]> refused = [];
        foreach (var report in reports)
        {
            try
            {
                await vendor.WriteConfigurationAsync(report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                refused.Add(report);
                DeviceTrace.Write(DeviceTraceLevel.Warn, "controller", $"table {report[2]:X2}/{report[3]:X2} "
                                                                       + DiagnosticText.FromException("refused", ex));
            }
        }

        return refused;
    }

    /// <summary>Starts the reader and hands the pad's identities, and so the hiding, to the host.</summary>
    private async ValueTask StartReadingAsync(AllyControllerTopology topology, CancellationToken cancellationToken)
    {
        await source.StartAsync(topology, PublishSampleAsync, OnReaderFault, cancellationToken).ConfigureAwait(false);
        await host.PublishPhysicalDevicesAsync(topology.PhysicalDevices, OutputCapabilities, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Writes the factory M1/M2 tables back.</summary>
    /// <returns>Null when every table was acknowledged, otherwise why the release is unverified.</returns>
    /// <remarks>
    ///     The MCU tables cannot be read back, so an acknowledged write is the whole of what can be
    ///     known, as it is for HC, which writes them and ignores the result (<c>ROGAlly.cs:646-668</c>).
    ///     Reporting that as unverified made every Ally release unverified, and the host blocks a
    ///     restart after an unverified release.
    /// </remarks>
    private async ValueTask<CapabilityReason?> RestoreConfigurationAsync(Deadline deadline)
    {
        _configured = false;
        if (!DeviceWriteBudget.IsAvailable(deadline))
        {
            // Nothing was written, so the entry stays pending and the next cycle restores the tables.
            return new CapabilityReason(CapabilityReasonCode.Quiescing,
                "Not enough time to write the factory controller tables back; the next start writes them.");
        }

        var refused = await WriteTablesAsync(AllyProtocol.DefaultConfiguration, CancellationToken.None)
            .ConfigureAwait(false);
        if (refused.Count > 0)
        {
            return new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                $"{refused.Count} factory controller tables could not be written back.");
        }

        // Every report was acknowledged; nothing more can be done for an unreadable table, so the
        // journal entry is cleared rather than left to block the next cycle.
        await journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoredVerified, CancellationToken.None)
            .ConfigureAwait(false);
        return null;
    }

    private ValueTask PublishSampleAsync(CanonicalControllerSample sample, CancellationToken cancellationToken)
    {
        return host.PublishControllerSampleAsync(sample with { Motion = motion.Current(sample.Timestamp) },
            cancellationToken);
    }

    private void OnReaderFault(Exception exception)
    {
        // Any reader failure is the pad going away, never a device fault: HC's read loop treats every
        // failure as Device_Removed and takes the pad again when it returns, and so does this. Faulting
        // here took fans, TDP and the OEM buttons down with the pad.
        var detail = DiagnosticText.FromException("The controller reader stopped", exception);
        if (State is not DeviceServiceState.Owned)
        {
            return;
        }

        DeviceTrace.Write(DeviceTraceLevel.Warn, "controller", detail + "; waiting for the pad to come back.");
        _ = Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        if (_context is { } context)
        {
            _ = NeutralizeReaderAndReconnectAsync(context);
        }
    }

    private async Task NeutralizeReaderAndReconnectAsync(DeviceCycleContext<AllyIdentityState> context)
    {
        try
        {
            if (_context == context && State is DeviceServiceState.Degraded)
            {
                await host.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow),
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
                _reconnect.Start(AttachWhenBackAsync, OnReconnectFailed);
            }
        }
    }

    /// <summary>
    ///     Taking the pad again threw. The tables are not written again: the service faults until the user
    ///     turns controller management on again or the next cycle acquires it.
    /// </summary>
    private void OnReconnectFailed(Exception exception)
    {
        var detail = DiagnosticText.FromException("Taking the pad again failed", exception);
        DeviceTrace.Write(DeviceTraceLevel.Error, "controller",
            detail + "; turn controller management off and on to try again.");
        Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    /// <summary>Takes the pad once it is back: tables again, reader again, identities if they changed.</summary>
    /// <returns>False only while the pad is still absent; the tables are written once for each return.</returns>
    private async ValueTask<bool> AttachWhenBackAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _context is not { } context)
        {
            return true;
        }

        var topology = await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (topology is null)
        {
            return false;
        }

        await source.StopAsync(cancellationToken).ConfigureAwait(false);
        // The pad came back with the firmware's own tables, as HC finds on Device_Inserted.
        await ConfigureAsync(context with { Deadline = Deadline.After(TimeSpan.FromSeconds(5)) }, cancellationToken)
            .ConfigureAwait(false);
        _topology = topology;
        await StartReadingAsync(topology, cancellationToken).ConfigureAwait(false);
        DeviceTrace.Write(DeviceTraceLevel.Info, "controller", $"the pad is back: {topology.Observed}.");
        _ = Set(DeviceServiceState.Owned);
        return true;
    }
}
