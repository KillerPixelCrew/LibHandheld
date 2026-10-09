namespace LibHandheld.Internal.Testing;

internal static class TestRecovery
{
    internal static DeviceRecoveryJournal Open(string directory)
    {
        return DeviceRecoveryJournal.OpenAsync(directory, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }
}
