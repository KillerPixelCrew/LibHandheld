// SPDX-License-Identifier: MIT

using System.Diagnostics;

namespace LibHandheld.Testing;

/// <summary>Waits for a condition that background work makes true.</summary>
/// <remarks>Linked into several test projects, so it uses no test-framework API.</remarks>
internal static class AsyncConditions
{
    /// <summary>How long any awaited condition or signal may take before the test fails.</summary>
    internal static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    /// <summary>Polls <paramref name="condition" /> until it holds.</summary>
    /// <exception cref="TimeoutException">The condition did not hold within <see cref="TimeLimit" />.</exception>
    internal static Task WaitForAsync(Func<bool> condition)
    {
        return WaitForAsync(() => Task.FromResult(condition()));
    }

    /// <summary>Polls an asynchronous <paramref name="condition" /> until it holds.</summary>
    /// <exception cref="TimeoutException">The condition did not hold within <see cref="TimeLimit" />.</exception>
    internal static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!await condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeLimit)
            {
                throw new TimeoutException($"The awaited condition did not hold within {TimeLimit.TotalSeconds:0} s.");
            }

            await Task.Delay(10);
        }
    }
}
