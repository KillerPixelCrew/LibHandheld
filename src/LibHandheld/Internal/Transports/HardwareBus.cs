namespace LibHandheld.Internal.Transports;

internal static class HardwareBus
{
    internal const string EmbeddedController = @"Global\Access_EC";
    internal const string IndexedIo = @"Global\Access_ISABUS.HTP.Method";
    internal const string Pci = @"Global\Access_PCI";

    internal static T Run<T>(string name, TimeSpan timeout, Func<T> operation)
    {
        using var mutex = new Mutex(false, name);
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
}
