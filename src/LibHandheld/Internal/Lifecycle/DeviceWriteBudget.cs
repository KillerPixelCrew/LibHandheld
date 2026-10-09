namespace LibHandheld.Internal.Lifecycle;

/// <summary>The time a deadline must still leave before a plugin starts a hardware write.</summary>
/// <remarks>
///     Two seconds covers a recovery-record flush and one bounded firmware exchange. A write the host
///     cannot wait for would end uncertain, so a shorter deadline refuses the write before anything is
///     sent. A command refused this way is rejected and may be retried; a release refused this way leaves
///     its recovery entry pending for the next start.
/// </remarks>
internal static class DeviceWriteBudget
{
    /// <summary>The time a deadline must still leave for a write.</summary>
    public static TimeSpan Minimum { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Whether a deadline leaves enough time for a write.</summary>
    /// <param name="deadline">The operation's deadline.</param>
    /// <returns>True when at least <see cref="Minimum" /> remains.</returns>
    public static bool IsAvailable(Deadline deadline)
    {
        return deadline.Remaining >= Minimum;
    }

    /// <summary>Refuses a write the deadline leaves too little time for.</summary>
    /// <param name="deadline">The operation's deadline.</param>
    /// <param name="operation">What was about to be written, for the message.</param>
    /// <exception cref="DeviceWriteBudgetException">Less than <see cref="Minimum" /> remains.</exception>
    public static void Require(Deadline deadline, string operation)
    {
        if (!IsAvailable(deadline))
        {
            throw new DeviceWriteBudgetException($"Insufficient budget for {operation}; nothing was written.");
        }
    }
}

/// <summary>A write was refused for lack of time before anything reached the hardware.</summary>
/// <param name="message">Why the write was refused.</param>
/// <remarks>Not an <see cref="OperationCanceledException" />: nobody cancelled anything, and nothing was written.</remarks>
internal sealed class DeviceWriteBudgetException(string message) : Exception(message);
