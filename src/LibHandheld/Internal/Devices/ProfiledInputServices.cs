using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Internal.Devices;

internal sealed class ProfiledMotionService(StandardMotionSource source)
    : DeviceService<ProfiledCycleIdentity>("motion")
{
    public override bool Suspendable => true;

    public override ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(source.Start()
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, Missing("Windows exposes no motion sensor pair.")));
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        source.Dispose();
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }
}

internal sealed class ProfiledTouchService(StandardControllerTouchpad source)
    : DeviceService<ProfiledCycleIdentity>("touchpad")
{
    internal bool NeedsReconnect => !source.IsConnected;
    public override bool Suspendable => true;

    public override ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(source.Start()
            ? Set(DeviceServiceState.Owned)
            : Set(DeviceServiceState.Passive, Missing("The model's digitizer is unavailable.")));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        await source.StopAsync(token).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }
}

internal sealed class ProfiledOemService(
    ProfiledDeviceModel profile,
    Action<string, CanonicalButtons, bool> publish,
    Func<bool> manage)
    : DeviceService<ProfiledCycleIdentity>("oem")
{
    private KeyboardOemSource? _keyboard;
    private IProfiledOemSource? _native;
    private bool _nativeReady;
    internal bool NeedsReconnect => _nativeReady && _native is { IsConnected: false };
    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        _native = manage() || !profile.NativeOemRequiresController ? profile.CreateOemSource?.Invoke() : null;
        var nativeReady = _native is not null && await _native.StartAsync(publish, token).ConfigureAwait(false);
        _nativeReady = nativeReady;
        var chords = profile.KeyboardChords.Where(chord => (manage() || !chord.RequiresController)
                                                           && (!nativeReady ||
                                                               !profile.NativeOemReplacesChords.Contains(chord.Id)))
            .ToArray();
        _keyboard = new KeyboardOemSource(chords);
        var keyboardReady = await _keyboard.StartAsync(publish, token).ConfigureAwait(false);
        return Set(nativeReady || keyboardReady ? DeviceServiceState.Owned : DeviceServiceState.Passive);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        try
        {
            if (_native is not null)
            {
                await _native.StopAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_keyboard is not null)
            {
                await _keyboard.StopAsync(token).ConfigureAwait(false);
            }
        }

        _native = null;
        _keyboard = null;
        _nativeReady = false;
        return Set(DeviceServiceState.Idle);
    }
}

/// <summary>Re-discovers the same physical topology after detach; never retries device writes.</summary>
internal sealed class ProfiledControllerService(
    StandardControllerSource source,
    IDeviceSink sink,
    Func<CanonicalButtons> buttons,
    Func<MotionSample?> motion,
    Func<StandardTouchSample>? touchpad = null)
    : DeviceService<ProfiledCycleIdentity>("controller")
{
    private volatile bool _connected;
    private CancellationTokenSource? _stop;
    private Task? _supervisor;
    private bool _supportsRumble;
    public override bool Suspendable => true;
    internal bool Connected => _connected;
    internal bool SupportsRumble => _connected && _supportsRumble;

    internal void Rumble(HapticOutputFrame frame)
    {
        source.Rumble(frame.LowFrequency, frame.HighFrequency);
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await ConnectAsync(token).ConfigureAwait(false);
        _stop = new CancellationTokenSource();
        _supervisor = SuperviseAsync(_stop.Token);
        return Set(_connected ? DeviceServiceState.Owned : DeviceServiceState.Degraded,
            _connected
                ? null
                : Missing("The exact physical controller is absent or ambiguous; waiting for it to return."));
    }

    private async ValueTask ConnectAsync(CancellationToken token)
    {
        if (source.Discover() is not { } topology)
        {
            return;
        }

        _supportsRumble = topology.SupportsRumble;
        await sink.PublishPhysicalDevicesAsync(topology.Devices, new HapticCapabilities
        {
            LowFrequency = topology.SupportsRumble ? OutputChannelSupport.Native : OutputChannelSupport.Unsupported,
            HighFrequency = topology.SupportsRumble ? OutputChannelSupport.Native : OutputChannelSupport.Unsupported
        }, token).ConfigureAwait(false);
        _connected = true;
        try
        {
            source.Start(topology, buttons, motion, sink.PublishControllerSampleAsync, _ => _connected = false,
                touchpad);
        }
        catch
        {
            _connected = false;
            throw;
        }
    }

    private async Task SuperviseAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_connected)
                {
                    continue;
                }

                await source.StopAsync(token).ConfigureAwait(false);
                await sink.PublishPhysicalDevicesAsync([], null, token).ConfigureAwait(false);
                try
                {
                    await ConnectAsync(token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException &&
                                                  !token.IsCancellationRequested)
                {
                    sink.TraceChange(DeviceTraceLevel.Warn, "controller", "reconnect", exception.Message);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _connected = false;
            Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted, exception.Message));
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> context,
        CancellationToken token)
    {
        _stop?.Cancel();
        if (_supervisor is not null)
        {
            await _supervisor.WaitAsync(token).ConfigureAwait(false);
        }

        await source.StopAsync(token).ConfigureAwait(false);
        _connected = false;
        await sink.PublishControllerSampleAsync(CanonicalControllerSample.Neutral(DateTimeOffset.UtcNow), token)
            .ConfigureAwait(false);
        await sink.PublishPhysicalDevicesAsync([], null, token).ConfigureAwait(false);
        _stop?.Dispose();
        _stop = null;
        _supervisor = null;
        return Set(DeviceServiceState.Idle);
    }
}
