using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Devices;

/// <summary>
///     Reads the power button from the Intel HID Event Filter's Portable Device Control collection
///     (Generic Desktop 0x0D, button System Power Down 0x81) and publishes its press and release.
/// </summary>
/// <remarks>
///     Windows' own path reaches the power button through the Button Converter, which reports a short
///     press only after release and as a press and release together, so it carries no hold duration. The
///     PDC collection reports both edges as they happen and opens for shared reading without
///     administrator rights. Measured on the Atlas 8 PA08-I51, 2026-11-01 (see the Acer provenance).
///     The collection is found by its capabilities, not a hardware ID, so another Intel model with the
///     same filter can opt in. The control is published only while the reader runs. A reader that stops
///     on its own is reopened in the background rather than faulting the device cycle.
/// </remarks>
internal sealed class IntelPdcPowerButtonService(ProfiledFeatureContext context)
    : ProfiledFeatureService("intel-pdc-power-button")
{
    /// <summary>The published control's identifier.</summary>
    internal const string ControlId = "power";

    private const ushort GenericDesktop = 0x01;
    private const ushort PortableDeviceControl = 0x0D;
    private const ushort SystemPowerDown = 0x81;
    private static readonly TimeSpan ReopenDelay = TimeSpan.FromSeconds(2);

    private static readonly OemControlDescriptor Control =
        CapabilityDescriptors.OemControl(ControlId, "Power button", OemControlPlacement.Power, false);

    private readonly Lock _gate = new();
    private readonly HidReportSource _source = new();
    private PowerButtonEdges? _edges;
    private HidUsageReader? _usages;
    private CancellationTokenSource? _lifetime;
    private bool _opened;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => [];
    internal override IReadOnlyList<CapabilityState> States => [];
    internal override IReadOnlyList<OemControlDescriptor> OemControls => _opened ? [Control] : [];

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
            "The power button has no controls."));
    }

    /// <summary>Finds the one PDC collection and starts reading it; a missing or ambiguous collection is passive.</summary>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await StopReaderAsync(token).ConfigureAwait(false);
        _edges = new PowerButtonEdges(Publish);
        _lifetime = new CancellationTokenSource();
        _opened = TryOpen(token, out var reason);
        return _opened
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, Missing(reason));
    }

    /// <summary>Stops the reader; a held press is released so the host never keeps a stuck edge.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        await StopReaderAsync(token).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    internal override async ValueTask DisposeAsync()
    {
        await StopReaderAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private bool TryOpen(CancellationToken token, out string reason)
    {
        HidCollection? collection;
        try
        {
            collection = HidDevices.Unique(HidDevices.EnumerateAll(), item => item is
            {
                UsagePage: GenericDesktop, Usage: PortableDeviceControl, InputLength: > 0
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reason = $"HID collections could not be listed: {exception.Message}";
            return false;
        }

        if (collection is null)
        {
            reason = "No single Intel Portable Device Control collection is present.";
            return false;
        }

        FileStream? stream = null;
        HidUsageReader? usages = null;
        try
        {
            stream = HidDevices.OpenStream(collection);
            usages = new HidUsageReader(stream.SafeFileHandle);
            lock (_gate)
            {
                _usages?.Dispose();
                _usages = usages;
            }

            if (!_source.Start(stream, collection.InputLength, OnReport, OnStopped, token))
            {
                reason = "The power button reader did not start.";
                return false;
            }

            DeviceTrace.Info(ServiceId, $"Reading the power button from {collection.Describe()}");
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            stream?.Dispose();
            usages?.Dispose();
            reason = $"The power button collection could not be opened: {exception.Message}";
            return false;
        }
    }

    private void OnReport(ReadOnlySpan<byte> report)
    {
        bool pressed;
        lock (_gate)
        {
            if (_usages is null)
            {
                return;
            }

            pressed = _usages.Usages(report, GenericDesktop).Contains(SystemPowerDown);
        }

        _edges?.Report(pressed);
    }

    /// <summary>Releases a held press and, unless the service is stopping, reopens the collection later.</summary>
    private void OnStopped()
    {
        _edges?.Stop();
        var lifetime = _lifetime;
        if (lifetime is null || lifetime.IsCancellationRequested)
        {
            return;
        }

        DeviceTrace.Warn(ServiceId, "The power button reader stopped; reopening it");
        _ = ReopenAsync(lifetime.Token);
    }

    private async Task ReopenAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(ReopenDelay, token).ConfigureAwait(false);
                await _source.StopAsync(token).ConfigureAwait(false);
                if (TryOpen(token, out var reason))
                {
                    return;
                }

                DeviceTrace.Warn(ServiceId, reason);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async ValueTask StopReaderAsync(CancellationToken token)
    {
        if (_lifetime is { } lifetime)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            lifetime.Dispose();
            _lifetime = null;
        }

        await _source.StopAsync(token).ConfigureAwait(false);
        lock (_gate)
        {
            _usages?.Dispose();
            _usages = null;
        }

        _opened = false;
    }

    private void Publish(OemControlEdge edge, string pressId)
    {
        context.Sink.PublishOemEventAsync(new OemControlEvent(ControlId, OemPressKind.Short, DateTimeOffset.UtcNow,
                pressId, edge), CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }
}

/// <summary>Turns the power button's report stream into press and release edges with one ID per press.</summary>
/// <remarks>
///     The firmware sometimes reports only the release of a quick tap (measured: two of three taps one
///     second apart). A release with no press before it is published as a press and release with the same
///     ID, so the host still sees a short press. Repeated reports of an unchanged state publish nothing.
/// </remarks>
internal sealed class PowerButtonEdges(Action<OemControlEdge, string> publish)
{
    private readonly Lock _gate = new();
    private string? _held;
    private long _sequence;

    internal void Report(bool pressed)
    {
        string id;
        var releaseOnly = false;
        lock (_gate)
        {
            if (pressed)
            {
                if (_held is not null)
                {
                    return;
                }

                _held = id = $"{IntelPdcPowerButtonService.ControlId}:{++_sequence}";
            }
            else if (_held is not null)
            {
                id = _held;
                _held = null;
            }
            else
            {
                id = $"{IntelPdcPowerButtonService.ControlId}:{++_sequence}";
                releaseOnly = true;
            }
        }

        if (releaseOnly)
        {
            publish(OemControlEdge.Pressed, id);
        }

        publish(pressed ? OemControlEdge.Pressed : OemControlEdge.Released, id);
    }

    /// <summary>Releases a held press when the reader stops.</summary>
    internal void Stop()
    {
        string? id;
        lock (_gate)
        {
            id = _held;
            _held = null;
        }

        if (id is not null)
        {
            publish(OemControlEdge.Released, id);
        }
    }
}
