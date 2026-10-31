using LibHandheld.Contracts;
using LibHandheld.Internal.Lifecycle;

namespace LibHandheld.Internal.Capabilities;

/// <summary>Builds native device command results.</summary>
/// <remarks>Each result is stamped with the time it is built, which is when the command finished.</remarks>
internal static class CommandResults
{
    /// <summary>The result of a command whose native write path threw.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="dispatched">Whether a write had begun, which makes its effect unknown.</param>
    /// <param name="exception">What the write path threw.</param>
    /// <returns>
    ///     <see cref="CommandOutcome.Indeterminate" /> once a write began, otherwise <see cref="CommandOutcome.Rejected" />.
    ///     Cancellation and an exhausted write budget are <see cref="CapabilityReasonCode.Quiescing" />, and such a
    ///     rejection may be retried because nothing was written; every other exception is
    ///     <see cref="CapabilityReasonCode.TransportFaulted" />.
    /// </returns>
    internal static CapabilityCommandResult TransportFailure(CapabilityCommand command, bool dispatched,
        Exception exception)
    {
        var code = exception is OperationCanceledException or DeviceWriteBudgetException
            ? CapabilityReasonCode.Quiescing
            : CapabilityReasonCode.TransportFaulted;
        return dispatched
            ? Indeterminate(command, code, exception.Message)
            : Rejected(command, code, exception.Message, retryable: code == CapabilityReasonCode.Quiescing);
    }

    /// <summary>A write acknowledged by its native transport, independent of optional readback.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <returns>The accepted write result.</returns>
    public static CapabilityCommandResult Applied(CapabilityCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Applied,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A command refused before anything was written.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="code">Why it was refused.</param>
    /// <param name="detail">The refusal in words.</param>
    /// <param name="retryable">Whether the same command may succeed later.</param>
    /// <returns>A <see cref="CommandOutcome.Rejected" /> result.</returns>
    public static CapabilityCommandResult Rejected(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail,
        bool retryable = false)
    {
        return Rejected(command, new CapabilityReason(code, detail, retryable));
    }

    /// <summary>A command refused before anything was written.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>A <see cref="CommandOutcome.Rejected" /> result.</returns>
    public static CapabilityCommandResult Rejected(CapabilityCommand command, CapabilityReason reason)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Rejected,
            Reason = reason,
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>A command whose effect is unknown because it failed after the write began.</summary>
    /// <param name="command">The command the result answers.</param>
    /// <param name="code">Why the outcome is unknown.</param>
    /// <param name="detail">The failure in words.</param>
    /// <returns>An <see cref="CommandOutcome.Indeterminate" /> result.</returns>
    public static CapabilityCommandResult Indeterminate(
        CapabilityCommand command,
        CapabilityReasonCode code,
        string detail)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new CapabilityCommandResult
        {
            CommandId = command.CommandId,
            Outcome = CommandOutcome.Indeterminate,
            Reason = new CapabilityReason(code, detail),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }
}
