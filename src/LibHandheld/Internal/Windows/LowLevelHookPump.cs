using System.Threading.Channels;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Windows;

/// <summary>A low-level hook whose callback queues edges that one pump publishes off the hook thread.</summary>
/// <typeparam name="TEdge">One queued edge.</typeparam>
/// <remarks>
///     Stop removes the hook first, so the release edges the hook thread queues while it unhooks reach the
///     channel before it completes, and the pump drains them. A hook fault marks the source disconnected,
///     queues the release edges and ends the pump, which traces the fault; the next stop then succeeds and
///     the owner can start again. The hook callback only reads state and queues edges.
/// </remarks>
internal sealed class LowLevelHookPump<TEdge> : IAsyncDisposable
{
    private readonly LowLevelHook _hook;
    private readonly KeyboardHookHandler? _keyboard;
    private readonly MouseHookHandler? _mouse;
    private readonly Action _releaseHeld;
    private readonly Action _reset;
    private readonly string _threadName;
    private Channel<TEdge>? _edges;
    private volatile bool _failed;
    private Task? _pump;
    private volatile bool _running;

    /// <summary>Creates a keyboard hook pump that is not started yet.</summary>
    /// <param name="threadName">The hook thread's name, for diagnostics.</param>
    /// <param name="keyboard">Decides each key event on the hook thread.</param>
    /// <param name="releaseHeld">Queues a release for every held edge; runs on the hook thread.</param>
    /// <param name="reset">Clears the owner's edge state once the pump ended.</param>
    public LowLevelHookPump(string threadName, KeyboardHookHandler keyboard, Action releaseHeld, Action reset)
        : this(hookId: 13, threadName, releaseHeld, reset)
    {
        _keyboard = keyboard;
    }

    /// <summary>Creates a mouse hook pump that is not started yet.</summary>
    /// <param name="threadName">The hook thread's name, for diagnostics.</param>
    /// <param name="mouse">Decides each mouse event on the hook thread.</param>
    /// <param name="releaseHeld">Queues a release for every held edge; runs on the hook thread.</param>
    /// <param name="reset">Clears the owner's edge state once the pump ended.</param>
    public LowLevelHookPump(string threadName, MouseHookHandler mouse, Action releaseHeld, Action reset)
        : this(hookId: 14, threadName, releaseHeld, reset)
    {
        _mouse = mouse;
    }

    private LowLevelHookPump(int hookId, string threadName, Action releaseHeld, Action reset)
    {
        _hook = new LowLevelHook(hookId, threadName);
        _threadName = threadName;
        _releaseHeld = releaseHeld;
        _reset = reset;
    }

    /// <summary>Whether the source runs and its hook has not faulted.</summary>
    public bool IsConnected => _running && !_failed;

    /// <summary>Whether the running hook faulted; its handler then passes every event on untouched.</summary>
    public bool Failed => _failed;

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    /// <summary>Queues one edge for the pump; called from the hook thread.</summary>
    /// <param name="edge">The edge.</param>
    public void Write(TEdge edge)
    {
        _edges?.Writer.TryWrite(edge);
    }

    /// <summary>Starts the pump and installs the hook.</summary>
    /// <param name="publish">Publishes one edge on the pump.</param>
    /// <param name="install">False when no event can match: the source then reads connected without a hook.</param>
    /// <param name="cancellationToken">Cancels installation; a canceled start stops what it began.</param>
    /// <returns>True when the hook runs or is not needed; false when it could not be installed.</returns>
    public async ValueTask<bool> StartAsync(Action<TEdge> publish, bool install, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _failed = false;
        if (!install)
        {
            _running = true;
            return true;
        }

        var edges = Channel.CreateUnbounded<TEdge>(new UnboundedChannelOptions
            { SingleReader = true, SingleWriter = true });
        _edges = edges;
        _pump = Task.Run(() => PumpAsync(edges.Reader, publish), CancellationToken.None);
        try
        {
            _running = _keyboard is not null
                ? await _hook.StartAsync(_keyboard, Fault, cancellationToken, stopped: _releaseHeld)
                    .ConfigureAwait(false)
                : await _hook.StartMouseAsync(_mouse!, Fault, cancellationToken, _releaseHeld).ConfigureAwait(false);
            if (!_running)
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return _running;
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        void Fault(Exception exception)
        {
            _failed = true;
            _releaseHeld();
            edges.Writer.TryComplete(exception);
        }
    }

    /// <summary>Removes the hook, then drains and ends the pump.</summary>
    /// <param name="cancellationToken">Cancels waiting; the owner's state is cleared once the pump ended.</param>
    /// <returns>Completion after the pump ended.</returns>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _hook.StopAsync(cancellationToken).ConfigureAwait(false);
        _running = false;
        _edges?.Writer.TryComplete();
        try
        {
            if (_pump is not null)
            {
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_pump is null || _pump.IsCompleted)
            {
                _pump = null;
                _edges = null;
                _reset();
            }
        }
    }

    private async Task PumpAsync(ChannelReader<TEdge> reader, Action<TEdge> publish)
    {
        try
        {
            await foreach (var edge in reader.ReadAllAsync().ConfigureAwait(false))
            {
                publish(edge);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A fault ends the source. Tracing it here keeps the stop that follows from failing on it,
            // which would leave the service faulted instead of reconnecting.
            _failed = true;
            DeviceTrace.Failure("oem", $"{_threadName} stopped", ex);
        }
    }
}
