using System.Text.Json;
using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Services;

/// <summary>One device's captured originals. Recovery bookkeeping never blocks a hardware command.</summary>
internal sealed class DeviceRecoveryJournal
{
    private readonly Dictionary<(string ServiceId, string Firmware), RecoveryOriginal?> _cycleCaptures = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RecoveryOriginal> _originals = new(StringComparer.Ordinal);
    private string? _path;

    public IReadOnlyList<RecoveryOriginal> OutstandingEntries => [.. _originals.Values];

    public static async ValueTask<DeviceRecoveryJournal> OpenAsync(string directory, CancellationToken token)
    {
        var journal = new DeviceRecoveryJournal();
        try
        {
            journal._path = Path.Combine(Path.GetFullPath(directory), "recovery.json");
            if (File.Exists(journal._path))
            {
                await using var stream = File.OpenRead(journal._path);
                var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, RecoveryOriginal>>(stream,
                    cancellationToken: token).ConfigureAwait(false);
                if (entries is not null)
                {
                    foreach (var (id, entry) in entries)
                    {
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(entry.FirmwareIdentity)
                                                           && entry.OriginalState.ValueKind == JsonValueKind.Object)
                        {
                            journal._originals[id] = entry with { ServiceId = id };
                        }
                        else
                        {
                            Log($"Ignoring incomplete recovery original for '{id}'.");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Cannot load recovery originals: {exception.Message}. Hardware commands remain available.");
        }

        return journal;
    }

    public DeviceRecoveryEntry<T>? EntryFor<T>(string id) where T : class
    {
        if (!_originals.TryGetValue(id, out var original))
        {
            return null;
        }

        if (original.StateType != typeof(T).FullName)
        {
            return null;
        }

        try
        {
            var state = original.OriginalState.Deserialize<T>();
            return state is null ? null : new DeviceRecoveryEntry<T>(id, original.FirmwareIdentity, state);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Cannot read recovery original for '{id}': {exception.Message}.");
            return null;
        }
    }

    public IReadOnlyList<DeviceRecoveryEntry<T>> Entries<T>() where T : class
    {
        return _originals.Keys.Select(EntryFor<T>).OfType<DeviceRecoveryEntry<T>>().ToArray();
    }

    public T? OriginalStateFor<T>(string id) where T : class
    {
        return EntryFor<T>(id)?.OriginalState;
    }

    public bool HasUnrestoredMutation(string id)
    {
        return _originals.ContainsKey(id);
    }

    public bool HoldsOriginal(string id, string firmware)
    {
        return _originals.TryGetValue(id, out var entry) && entry.FirmwareIdentity == firmware;
    }

    /// <summary>Starts a newly acquired cycle without discarding originals still awaiting restoration.</summary>
    public async ValueTask BeginCycleAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            _cycleCaptures.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restores an outstanding original once, or retires one captured under other firmware.</summary>
    /// <remarks>
    ///     The restore callback retires the entry itself when its write was accepted; a failed restore is traced
    ///     and the entry stays pending for the next release or start. Retiring a stale entry writes no hardware,
    ///     so it is not cancelled with the start.
    /// </remarks>
    public async ValueTask ReconcileAsync<T>(string id, string firmware,
        Func<T, CancellationToken, ValueTask> restore, CancellationToken token) where T : class
    {
        if (EntryFor<T>(id) is not { } entry)
        {
            return;
        }

        if (entry.FirmwareIdentity != firmware)
        {
            await RetireAsync(id, $"bound to {entry.FirmwareIdentity}, the firmware now reads {firmware}",
                CancellationToken.None).ConfigureAwait(false);
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            await restore(entry.OriginalState, token).ConfigureAwait(false);
            if (!_originals.ContainsKey(id))
            {
                DeviceTrace.Info("recovery", $"outstanding '{id}' restored.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log(
                $"Cannot restore original for '{id}': {exception.Message}. Original retained; explicit commands remain available.");
        }
    }

    public ValueTask<DeviceRecoveryOperation<T>?> BeginAsync<T>(string id, string firmware, T state,
        CancellationToken token) where T : class
    {
        return BeginAsync(id, firmware, () => state, token);
    }

    public ValueTask<DeviceRecoveryOperation<T>?> BeginAsync<T>(string id, string firmware, Func<T?> capture,
        CancellationToken token) where T : class
    {
        return BeginAsync<T>(id, firmware, _ => ValueTask.FromResult(capture()), token);
    }

    /// <summary>Attempts capture once per service and firmware in the acquired cycle, including failed captures.</summary>
    public async ValueTask<DeviceRecoveryOperation<T>?> BeginAsync<T>(string id, string firmware,
        Func<CancellationToken, ValueTask<T?>> capture, CancellationToken token) where T : class
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (HoldsOriginal(id, firmware) && EntryFor<T>(id) is { } existing)
            {
                // Only this cycle's in-memory capture changes; the persisted originals stay as they are.
                _cycleCaptures[(id, firmware)] = _originals[id];
                return new DeviceRecoveryOperation<T>(existing, false);
            }

            var retired = _originals.Remove(id);
            if (retired)
            {
                Log($"Retired recovery original for '{id}': firmware changed; nothing restored.");
            }

            var key = (id, firmware);
            if (_cycleCaptures.TryGetValue(key, out var captured))
            {
                if (captured is null)
                {
                    return null;
                }

                // Re-enlist the first original after a successful restore in this same cycle.
                // A subsequent write must not capture our previous target as its original.
                _originals[id] = captured;
                await SaveAsync(token).ConfigureAwait(false);
                return EntryFor<T>(id) is { } prior
                    ? new DeviceRecoveryOperation<T>(prior, true)
                    : null;
            }

            _cycleCaptures[key] = null;

            try
            {
                var state = await capture(token).ConfigureAwait(false);
                if (state is null)
                {
                    Log($"No readable recovery original for '{id}'; hardware write remains available.");
                    if (retired)
                    {
                        await SaveAsync(token).ConfigureAwait(false);
                    }

                    return null;
                }

                var original = new RecoveryOriginal(id, firmware, JsonSerializer.SerializeToElement(state),
                    typeof(T).FullName!);
                _originals[id] = original;
                _cycleCaptures[key] = original;
                await SaveAsync(token).ConfigureAwait(false);
                return new DeviceRecoveryOperation<T>(new DeviceRecoveryEntry<T>(id, firmware, state), true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (_cycleCaptures[key] is null)
                {
                    _cycleCaptures.Remove(key);
                }

                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log(
                    $"Cannot capture recovery original for '{id}': {exception.Message}. Hardware write remains available.");
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retires the original a command opened when that same command was refused before any write.</summary>
    /// <param name="operation">What <c>BeginAsync</c> returned for the command, or null when it journalled nothing.</param>
    /// <param name="result">The command's result.</param>
    /// <remarks>
    ///     A refusal proves nothing was written, so its original has nothing to restore and would otherwise make
    ///     release write the device back to the state it is already in. An applied or indeterminate command keeps
    ///     its original, and one opened earlier is never touched. The cycle's capture is kept, so the next command
    ///     journals that same first original again.
    /// </remarks>
    public async ValueTask DiscardUnusedAsync<T>(DeviceRecoveryOperation<T>? operation,
        CapabilityCommandResult result) where T : class
    {
        ArgumentNullException.ThrowIfNull(result);
        if (operation is { Opened: true } && result.Outcome is CommandOutcome.Rejected)
        {
            await MarkRestoredAsync(operation.Entry.ServiceId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>An accepted restore retires the original; a failed restore makes no call and keeps it.</summary>
    public async ValueTask MarkRestoredAsync(string id, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_originals.Remove(id))
            {
                await SaveAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retires an original whose restore write the transport accepted, logging the optional readback.</summary>
    /// <param name="id">The restored entry.</param>
    /// <param name="observe">
    ///     Reads the restored state back: true when it matches the original, false when it differs, null when the
    ///     device reports nothing comparable. It may throw.
    /// </param>
    /// <param name="token">Cancels the readback only; the entry is retired either way.</param>
    /// <remarks>
    ///     The readback is evidence for the log and gates nothing: the accepted write completes the restore, so a
    ///     mismatch or failed read neither keeps the entry nor causes a second write.
    /// </remarks>
    public async ValueTask CompleteRestoreAsync(string id, Func<CancellationToken, ValueTask<bool?>> observe,
        CancellationToken token)
    {
        try
        {
            var matched = await observe(token).ConfigureAwait(false);
            if (matched != true)
            {
                DeviceTrace.Warn("recovery", matched == false
                    ? $"'{id}' restore was accepted but reads back differently."
                    : $"'{id}' restore was accepted; the device reports nothing to compare.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn("recovery", $"'{id}' restore was accepted; its observation is unavailable: "
                                         + exception.Message);
        }

        await MarkRestoredAsync(id, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc cref="CompleteRestoreAsync(string, Func{CancellationToken, ValueTask{bool?}}, CancellationToken)" />
    public ValueTask CompleteRestoreAsync(string id, Func<bool?> observe)
    {
        return CompleteRestoreAsync(id, _ => ValueTask.FromResult(observe()), CancellationToken.None);
    }

    public async ValueTask RetireAsync(string id, string reason, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_originals.Remove(id))
            {
                return;
            }

            Log($"Retired recovery original for '{id}': {reason}; nothing restored.");
            await SaveAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask SaveAsync(CancellationToken token)
    {
        if (_path is null)
        {
            Log("Recovery path unavailable; keeping originals in memory.");
            return;
        }

        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, _originals, cancellationToken: token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporary, _path, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log($"Cannot save recovery originals: {exception.Message}. Hardware commands remain available.");
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void Log(string message)
    {
        DeviceTrace.Warn("recovery", message);
    }
}

internal sealed record RecoveryOriginal(
    string ServiceId,
    string FirmwareIdentity,
    JsonElement OriginalState,
    string StateType);

internal sealed record DeviceRecoveryEntry<T>(string ServiceId, string FirmwareIdentity, T OriginalState)
    where T : class;

internal sealed record DeviceRecoveryOperation<T>(DeviceRecoveryEntry<T> Entry, bool Opened) where T : class;
