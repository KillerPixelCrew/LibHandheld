namespace LibHandheld.Tests.Services;

public sealed class DeviceServiceStatusTests
{
    [Fact]
    public async Task StateAndReasonTimestampChangesOnlyForAnActualTransition()
    {
        var service = new Status();
        var reason = new CapabilityReason(CapabilityReasonCode.ResourceReleased, "Disabled");
        service.ApplyResult(new DeviceServiceResult(DeviceServiceState.Passive, reason));
        var at = service.ChangedAt;
        service.ApplyResult(new DeviceServiceResult(DeviceServiceState.Passive, reason with { }));
        Assert.Equal(at, service.ChangedAt);
        await Task.Delay(1);
        service.ApplyResult(new DeviceServiceResult(DeviceServiceState.Passive, reason with { Detail = "Changed" }));
        Assert.True(service.ChangedAt > at);
        at = service.ChangedAt;
        await Task.Delay(1);
        service.ApplyResult(new DeviceServiceResult(DeviceServiceState.Owned));
        Assert.True(service.ChangedAt > at);
        at = service.ChangedAt;
        service.ApplyResult(new DeviceServiceResult(DeviceServiceState.Owned));
        Assert.Equal(at, service.ChangedAt);
        await Task.Delay(1);
        service.ChangeState(DeviceServiceState.Idle);
        Assert.True(service.ChangedAt > at);
        at = service.ChangedAt;
        service.ChangeState(DeviceServiceState.Idle);
        Assert.Equal(at, service.ChangedAt);
    }

    private sealed class Status() : DeviceServiceStatus("fixture")
    {
        internal void ChangeState(DeviceServiceState state)
        {
            _ = Set(state);
        }
    }
}
