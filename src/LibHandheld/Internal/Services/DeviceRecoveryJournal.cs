using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Internal.Services;

/// <summary>Keeps the original temporary state a plugin must restore after a crash, one entry per service.</summary>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
/// <remarks>
///     The record is <c>temporary-state.v1.json</c> in the plugin state directory WSGM provides, replaced
///     atomically on every change. A package derives one journal, supplies the source-generated type
///     information for its state, validates its entries, and owns the policy for when an entry is restored.
///     A record another build of the package wrote loads as long as its entries validate, so the package's
///     JSON context should ignore members its state type does not declare.
///     A record that cannot be loaded, or a state directory that cannot be written when it is opened,
///     leaves <see cref="FailureReason" /> set and refuses changes. A failed save refuses its own
///     mutation; it does not permanently block later writes after a transient file lock has been released.
/// </remarks>
internal abstract class DeviceRecoveryJournal<TState>
    where TState : class
{
    private const int CurrentVersion = 1;
    private const string FileName = "temporary-state.v1.json";
    private readonly JsonTypeInfo<DeviceRecoveryDocument<TState>> _typeInfo;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private List<DeviceRecoveryEntry<TState>> _entries = [];
    private string? _path;

    /// <summary>Creates a journal that serializes its record with <paramref name="typeInfo" />.</summary>
    /// <param name="typeInfo">Source-generated type information for the package's document.</param>
    protected DeviceRecoveryJournal(JsonTypeInfo<DeviceRecoveryDocument<TState>> typeInfo)
    {
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
    }

    /// <summary>Why the record is unavailable, or null while it is readable and writable.</summary>
    public CapabilityReason? FailureReason { get; private set; }

    /// <summary>Every entry not yet restored.</summary>
    public IReadOnlyList<DeviceRecoveryEntry<TState>> OutstandingEntries => [.. _entries];

    /// <summary>The record's state as plugin diagnostics report it: <c>blocked</c>, <c>pending</c> or <c>healthy</c>.</summary>
    public string DiagnosticState =>
        FailureReason is not null ? "blocked" : _entries.Count == 0 ? "healthy" : "pending";

    /// <summary>The outstanding entry of a service, if it has one.</summary>
    /// <param name="serviceId">The service.</param>
    /// <returns>The entry, or null.</returns>
    public DeviceRecoveryEntry<TState>? EntryFor(string serviceId)
    {
        return _entries.SingleOrDefault(entry => string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal));
    }

    /// <summary>Whether a service has an outstanding entry.</summary>
    /// <param name="serviceId">The service.</param>
    /// <returns>True while an entry is outstanding.</returns>
    public bool HasUnrestoredMutation(string serviceId)
    {
        return EntryFor(serviceId) is not null;
    }

    /// <summary>The state captured immediately before a service's first mutation.</summary>
    /// <param name="serviceId">The service whose outstanding mutation is being restored.</param>
    /// <returns>The captured state, or null when the service has no outstanding mutation.</returns>
    public TState? OriginalStateFor(string serviceId)
    {
        return EntryFor(serviceId)?.OriginalState;
    }

    /// <summary>The original a release may write back: that of a pending entry only.</summary>
    /// <param name="serviceId">The service being released.</param>
    /// <returns>The captured state, or null when the service has no pending entry.</returns>
    public TState? PendingOriginalFor(string serviceId)
    {
        return EntryFor(serviceId) is { Status: DeviceRecoveryStatus.Pending } entry ? entry.OriginalState : null;
    }

    /// <summary>Records the state captured before a service's first mutation.</summary>
    /// <param name="serviceId">The service about to mutate.</param>
    /// <param name="firmwareIdentity">The firmware the captured state belongs to.</param>
    /// <param name="originalState">The state to restore.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     A new entry, opened, when the service has none or its entry belongs to other firmware, which
    ///     the fresh capture replaces. An entry on the same firmware keeps its first original and is not
    ///     opened; one whose restore was unverified or failed is set pending again, because the explicit
    ///     command that calls this is the user action that lets the next release write that original.
    /// </returns>
    /// <exception cref="InvalidOperationException">The record is unavailable.</exception>
    public async ValueTask<DeviceRecoveryOperation<TState>> BeginAsync(
        string serviceId,
        string firmwareIdentity,
        TState originalState,
        CancellationToken cancellationToken)
    {
        var entry = new DeviceRecoveryEntry<TState>
        {
            ServiceId = serviceId,
            FirmwareIdentity = firmwareIdentity,
            OriginalState = originalState,
            Status = DeviceRecoveryStatus.Pending
        };
        Validate(entry);
        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DeviceRecoveryEntry<TState>> others =
            [
                .. _entries.Where(item => !string.Equals(item.ServiceId, serviceId, StringComparison.Ordinal))
            ];
            if (EntryFor(serviceId) is { } existing
                && string.Equals(existing.FirmwareIdentity, firmwareIdentity, StringComparison.Ordinal))
            {
                if (existing.Status is DeviceRecoveryStatus.Pending)
                {
                    return new DeviceRecoveryOperation<TState>(existing, false);
                }

                var rearmed = existing with { Status = DeviceRecoveryStatus.Pending };
                await SaveAsync([.. others, rearmed], cancellationToken).ConfigureAwait(false);
                return new DeviceRecoveryOperation<TState>(rearmed, false);
            }

            await SaveAsync([.. others, entry], cancellationToken).ConfigureAwait(false);
            return new DeviceRecoveryOperation<TState>(entry, true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Records what became of a service's entry.</summary>
    /// <param name="serviceId">The service.</param>
    /// <param name="status">
    ///     <see cref="DeviceRecoveryStatus.RestoredVerified" /> removes the entry; any other status is kept
    ///     with it. A service without an entry is left alone.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task completing once the record is written.</returns>
    public async ValueTask SetStatusAsync(
        string serviceId,
        DeviceRecoveryStatus status,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ThrowIfUnavailable();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<DeviceRecoveryEntry<TState>> entries = [.. _entries];
            var index = entries.FindIndex(entry => string.Equals(entry.ServiceId, serviceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            if (status is DeviceRecoveryStatus.RestoredVerified)
            {
                entries.RemoveAt(index);
            }
            else
            {
                entries[index] = entries[index] with { Status = status };
            }

            await SaveAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Opens the record in the plugin state directory, loads its entries and proves it writable.</summary>
    /// <param name="stateDirectory">The plugin state directory WSGM provided.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>A task completing once the entries are loaded or <see cref="FailureReason" /> is set.</returns>
    protected async ValueTask LoadAsync(string stateDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            FailureReason = Unavailable("WSGM did not provide a writable plugin state directory.");
            return;
        }

        try
        {
            var root = Path.GetFullPath(stateDirectory);
            Directory.CreateDirectory(root);
            _path = Path.Combine(root, FileName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = Unavailable($"The plugin recovery directory is unavailable ({ex.GetType().Name}).");
            return;
        }

        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync(stream, _typeInfo, cancellationToken)
                               .ConfigureAwait(false)
                           ?? throw new InvalidDataException("The recovery record was empty.");
            ValidateDocument(document);
            _entries = [.. document.Entries];
        }
        catch (FileNotFoundException)
        {
            // Only an absent record means there is no outstanding recovery obligation.
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = Unavailable($"The plugin recovery record is unavailable or invalid ({ex.GetType().Name}).");
            _entries = [];
            return;
        }

        // The temporary file a save writes proves the directory writable without touching the record,
        // so a start finds a refused directory before the first command needs it.
        var probe = _path + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(probe, [], cancellationToken).ConfigureAwait(false);
            File.Delete(probe);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FailureReason = Unavailable($"The plugin recovery record is not writable ({ex.GetType().Name}).");
        }
    }

    /// <summary>Checks that an entry's service, firmware identity and state belong together.</summary>
    /// <param name="entry">The entry being recorded or loaded.</param>
    /// <exception cref="InvalidDataException">The entry is not one this package could have written.</exception>
    protected abstract void ValidateEntry(DeviceRecoveryEntry<TState> entry);

    private static CapabilityReason Unavailable(string detail)
    {
        return new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail);
    }

    private async ValueTask SaveAsync(List<DeviceRecoveryEntry<TState>> entries, CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("The plugin recovery path is unavailable.");
        }

        var document = new DeviceRecoveryDocument<TState>
        {
            Version = CurrentVersion,
            Entries = [.. entries.OrderBy(entry => entry.ServiceId, StringComparer.Ordinal)]
        };
        ValidateDocument(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, _typeInfo);
        var temporary = _path + ".tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporary, _path, true);
            _entries = entries;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        if (FailureReason is not null || _path is null)
        {
            throw new InvalidOperationException(
                FailureReason?.Detail ?? "The plugin recovery record is unavailable.");
        }
    }

    private void ValidateDocument(DeviceRecoveryDocument<TState> document)
    {
        if (document.Version != CurrentVersion)
        {
            throw new InvalidDataException("The recovery record header is invalid.");
        }

        HashSet<string> services = new(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            Validate(entry);
            if (!services.Add(entry.ServiceId))
            {
                throw new InvalidDataException("The recovery record has duplicate services.");
            }
        }
    }

    private void Validate(DeviceRecoveryEntry<TState> entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.ServiceId)
            || string.IsNullOrWhiteSpace(entry.FirmwareIdentity)
            || entry.OriginalState is null
            || !Enum.IsDefined(entry.Status))
        {
            throw new InvalidDataException("A recovery entry is incomplete.");
        }

        ValidateEntry(entry);
    }
}

/// <summary>The recovery record as it is stored.</summary>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
internal sealed record DeviceRecoveryDocument<TState>
    where TState : class
{
    /// <summary>The record format version.</summary>
    public required int Version { get; init; }

    /// <summary>Every outstanding entry, ordered by service.</summary>
    public IReadOnlyList<DeviceRecoveryEntry<TState>> Entries { get; init; } = [];
}

/// <summary>One service's captured original and what became of its restore.</summary>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
internal sealed record DeviceRecoveryEntry<TState>
    where TState : class
{
    /// <summary>The service that mutated.</summary>
    public required string ServiceId { get; init; }

    /// <summary>The firmware the captured state belongs to.</summary>
    /// <remarks>Whether an entry is restored on other firmware is the package's decision.</remarks>
    public required string FirmwareIdentity { get; init; }

    /// <summary>The state captured immediately before the first mutation.</summary>
    public required TState OriginalState { get; init; }

    /// <summary>What became of the restore.</summary>
    public required DeviceRecoveryStatus Status { get; init; }
}

/// <summary>The entry a mutation runs under.</summary>
/// <param name="Entry">The outstanding entry.</param>
/// <param name="Opened">Whether this call created it; false when an earlier mutation's entry was kept.</param>
/// <typeparam name="TState">The package's own record of a captured original.</typeparam>
internal sealed record DeviceRecoveryOperation<TState>(DeviceRecoveryEntry<TState> Entry, bool Opened)
    where TState : class;

/// <summary>What became of a recovery entry's restore.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeviceRecoveryStatus>))]
internal enum DeviceRecoveryStatus
{
    /// <summary>The mutation is outstanding and the original has not been written back.</summary>
    Pending,

    /// <summary>The original was written back; the entry is removed.</summary>
    RestoredVerified,

    /// <summary>The original was written back but could not be confirmed.</summary>
    RestoredUnverified,

    /// <summary>Writing the original back failed.</summary>
    RestoreFailed
}
