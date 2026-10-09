namespace LibHandheld.Families.MsiClaw.Tests;

/// <summary>Skips HC's write spacing so the tests do not sleep through it.</summary>
internal static class TestTiming
{
    public static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = static (_, _) => Task.CompletedTask;
}
