using System.Collections.Concurrent;

namespace LibHandheld.Internal.Transports;

/// <summary>Serializes port and register access with other tools through the shared named bus mutexes.</summary>
/// <remarks>
///     Each bus mutex is opened on first use and kept for the process. A failed open throws to that caller
///     and caches nothing, so the next access tries again. The mutex is thread-affine and every operation
///     runs synchronously, so acquire and release stay on one thread.
/// </remarks>
internal static class HardwareBus
{
    internal const string EmbeddedController = @"Global\Access_EC";
    internal const string IndexedIo = @"Global\Access_ISABUS.HTP.Method";
    internal const string Pci = @"Global\Access_PCI";

    private static readonly ConcurrentDictionary<string, Mutex> Mutexes = new(StringComparer.Ordinal);

    internal static T Run<T>(string name, TimeSpan timeout, Func<T> operation)
    {
        var mutex = MutexFor(name);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException($"Timed out waiting for hardware bus {name}.");
            }

            return operation();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static Mutex MutexFor(string name)
    {
        if (Mutexes.TryGetValue(name, out var cached))
        {
            return cached;
        }

        Mutex created = new(initiallyOwned: false, name);
        if (Mutexes.TryAdd(name, created))
        {
            return created;
        }

        created.Dispose();
        return Mutexes[name];
    }
}
