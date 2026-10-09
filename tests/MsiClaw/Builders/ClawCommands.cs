namespace LibHandheld.Families.MsiClaw.Tests.Builders;

/// <summary>Capability commands addressed to the fake hardware's cycle.</summary>
internal static class ClawCommands
{
    internal const long FixtureId = 7;

    internal static CapabilityCommand Command(
        string capabilityId,
        string? instanceId,
        CapabilityValue value)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(),
            CapabilityId = capabilityId,
            InstanceId = instanceId,
            RequestedValue = value,
            Deadline = Deadline.After(TimeSpan.FromMinutes(1))
        };
    }
}
