using System.Diagnostics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Services;

/// <summary>
///     Runs a device's commands, lifecycle transitions and periodic observation one at a time, so a refresh
///     never interleaves with a hardware write.
/// </summary>
/// <remarks>
///     The device keeps its own lifetime state and supplies admission, owner maintenance and state publication
///     hooks, plus optional read-only sensor sampling. Maintenance never polls control values: accepted write
///     intent remains authoritative. A device that
///     begins to suspend or stop marks itself quiescing, calls <see cref="StopObservation" />, and then runs
///     the transition through <see cref="RunAsync" />, so a command or refresh already in flight finishes
///     first and nothing new starts.
/// </remarks>
internal sealed class DeviceCommandSerializer : IDisposable
{
    /// <summary>How often the owner performs maintenance and republishes accepted state.</summary>
    /// <remarks>Ten seconds bounds routine owner maintenance between explicit requests.</remarks>
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromSeconds(10);

    /// <summary>Read-only live measurements are refreshed inside the host's five-second freshness window.</summary>
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long a pass waits for an in-flight command before skipping; the command republishes.</summary>
    private static readonly TimeSpan ObservationCommandWait = TimeSpan.FromSeconds(2);

    /// <summary>The most a post-command refresh and publication may take, within the command's own deadline.</summary>
    private static readonly TimeSpan PostCommandLimit = TimeSpan.FromSeconds(2);

    private readonly Func<CapabilityDescriptorSet?> _acceptingSurface;
    private readonly string _deviceName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<CancellationToken, ValueTask> _publish;
    private readonly Func<string?, CancellationToken, ValueTask> _refresh;
    private readonly SemaphoreSlim _refreshRequested = new(0, 1);
    private readonly Func<CancellationToken, ValueTask>? _refreshTelemetry;
    private CancellationTokenSource? _observation;
    private CancellationToken _observationToken;

    /// <summary>Creates the serializer for one plugin instance.</summary>
    /// <param name="deviceName">The device family as command refusals name it, such as "Claw".</param>
    /// <param name="acceptingSurface">
    ///     The published descriptor set while the cycle accepts commands, or null while it is inactive or
    ///     quiescing.
    /// </param>
    /// <param name="refresh">
    ///     Updates accepted command state for the supplied capability, or performs owner maintenance and sensor
    ///     sampling when the id is null. Control values are never polled. An exception ends only this pass.
    /// </param>
    /// <param name="publish">Publishes every current capability state.</param>
    /// <param name="refreshTelemetry">Optional read-only live measurements; never settings or watchdog writes.</param>
    public DeviceCommandSerializer(
        string deviceName,
        Func<CapabilityDescriptorSet?> acceptingSurface,
        Func<string?, CancellationToken, ValueTask> refresh,
        Func<CancellationToken, ValueTask> publish,
        Func<CancellationToken, ValueTask>? refreshTelemetry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceName);
        _deviceName = deviceName;
        _acceptingSurface = acceptingSurface ?? throw new ArgumentNullException(nameof(acceptingSurface));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _refreshTelemetry = refreshTelemetry;
    }

    /// <summary>Requests observation cancellation without waiting for an in-flight operation to finish.</summary>
    /// <remarks>Does not dispose the serialization gate or prevent later calls; stop the owning cycle first.</remarks>
    public void Dispose()
    {
        StopObservation();
    }

    /// <summary>Runs a lifecycle transition once no command or refresh is in flight.</summary>
    /// <param name="operation">Transition run while holding the gate; it owns cancellation of its own work.</param>
    /// <param name="cancellationToken">Cancels gate acquisition only; it is not passed to the transition.</param>
    /// <returns>A task completing when the transition has finished.</returns>
    public async ValueTask RunAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs a lifecycle transition once no command or refresh is in flight.</summary>
    /// <param name="operation">Transition run while holding the gate; it owns cancellation of its own work.</param>
    /// <param name="cancellationToken">Cancels gate acquisition only; it is not passed to the transition.</param>
    /// <typeparam name="T">The transition's result type.</typeparam>
    /// <returns>The transition's result.</returns>
    public async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Admits a command, runs it in its serialized turn, and republishes what it changed.</summary>
    /// <param name="command">The command.</param>
    /// <param name="execute">
    ///     Revalidates deadline, value and service availability, then returns a truthful
    ///     result. The serializer does not impose these checks or translate exceptions from this callback.
    /// </param>
    /// <param name="cancellationToken">Cancels gate acquisition and is passed to execution and publication.</param>
    /// <returns>
    ///     Rejected if quiescing or canceled before gate acquisition; otherwise the callback's result, except
    ///     publication failure is diagnosed without changing an accepted native write outcome.
    /// </returns>
    /// <remarks>
    ///     A command is refused while the cycle is inactive or quiescing, both before and after it waits for
    ///     its turn. After an applied write accepted state is updated and republished within the command's
    ///     deadline, at most two seconds, so adjacent rows follow promptly. Publication failure is
    ///     logged and never changes an acknowledged hardware result.
    /// </remarks>
    public async ValueTask<CapabilityCommandResult> ExecuteAsync(
        CapabilityCommand command,
        Func<CancellationToken, ValueTask<CapabilityCommandResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(execute);
        if (_acceptingSurface() is null)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                $"The {_deviceName} device cycle is inactive or quiescing.");
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.Quiescing,
                "The command was cancelled before its serialized hardware turn began.");
        }

        try
        {
            if (_acceptingSurface() is null)
            {
                return CommandResults.Rejected(
                    command,
                    CapabilityReasonCode.Quiescing,
                    $"The {_deviceName} device cycle started quiescing before this command could run.");
            }

            var result = await execute(cancellationToken).ConfigureAwait(false);
            if (result.Outcome == CommandOutcome.Applied)
            {
                await RepublishAsync(command, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts serialized owner maintenance and sensor sampling, replacing any prior cycle's loop.</summary>
    public void StartObservation()
    {
        StopObservation();
        CancellationTokenSource loop = new();
        _observation = loop;
        _observationToken = loop.Token;
        _ = Task.Run(() => ObserveAsync(loop.Token), loop.Token);
    }

    internal void RequestObservation()
    {
        try
        {
            _refreshRequested.Release();
        }
        catch (SemaphoreFullException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Requests cancellation of periodic refresh and any post-command publication without joining them.</summary>
    /// <remarks>Use <see cref="RunAsync(Func{ValueTask}, CancellationToken)" /> to wait behind an in-flight operation.</remarks>
    public void StopObservation()
    {
        var loop = _observation;
        _observation = null;
        if (loop is null)
        {
            return;
        }

        try
        {
            loop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a concurrent stop; nothing left to cancel.
        }

        loop.Dispose();
    }

    /// <summary>Samples sensors, performs owner maintenance and republishes accepted state until the cycle ends.</summary>
    /// <param name="cancellationToken">Ends the loop.</param>
    /// <remarks>
    ///     A failed pass is traced and the loop continues: a device that cannot be read for one interval is a
    ///     stale reading, which WSGM already models, not a reason to stop observing.
    /// </remarks>
    private async Task ObserveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lastFullRefresh = Stopwatch.GetTimestamp();
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = ObservationInterval - Stopwatch.GetElapsedTime(lastFullRefresh);
                var delay = remaining <= TimeSpan.Zero
                    ? TimeSpan.Zero
                    : _refreshTelemetry is not null && remaining > TelemetryInterval
                        ? TelemetryInterval
                        : remaining;
                var requested = await _refreshRequested.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                if (_acceptingSurface() is null)
                {
                    lastFullRefresh = Stopwatch.GetTimestamp();
                    continue;
                }

                if (!await _gate.WaitAsync(ObservationCommandWait, cancellationToken).ConfigureAwait(false))
                {
                    if (requested)
                    {
                        RequestObservation();
                    }

                    await Task.Delay(ObservationCommandWait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    if (requested || Stopwatch.GetElapsedTime(lastFullRefresh) >= ObservationInterval)
                    {
                        lastFullRefresh = Stopwatch.GetTimestamp();
                        await _refresh(null, cancellationToken).ConfigureAwait(false);
                    }
                    if (_refreshTelemetry is not null)
                    {
                        // Maintenance does not necessarily sample sensors. Keep measurements fresh on
                        // that turn too; skipping it doubles the telemetry gap and trips host fan safety.
                        await _refreshTelemetry(cancellationToken).ConfigureAwait(false);
                    }

                    await _publish(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DeviceTrace.Failure("observe", "periodic observation refresh failed", ex);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The cycle ended while the loop waited.
        }
    }

    /// <summary>Updates accepted state and publishes after a command, bounded by its deadline and by quiescing.</summary>
    /// <returns>True once every state was published.</returns>
    private async ValueTask<bool> RepublishAsync(CapabilityCommand command, CancellationToken cancellationToken)
    {
        if (_acceptingSurface() is null)
        {
            // Suspend or stop has begun and publishes the final states itself.
            DeviceTrace.Info("observe", $"post-command refresh for '{command.CapabilityId}' skipped while quiescing.");
            return false;
        }

        using var bounded = command.Deadline.Earliest(Deadline.After(PostCommandLimit))
            .CreateCancellationSource(cancellationToken, _observationToken);
        try
        {
            bounded.Token.ThrowIfCancellationRequested();
            await _refresh(command.CapabilityId, bounded.Token).ConfigureAwait(false);
            await _publish(bounded.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("observe", $"post-command refresh for '{command.CapabilityId}' failed", ex);
            return false;
        }
    }
}
