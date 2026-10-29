using System.Buffers.Binary;
using System.Threading.Channels;
using LibHandheld.Families.Acer;
using LibHandheld.Internal.Devices;
using LibHandheld.Testing;

namespace LibHandheld.Tests.Acer;

public sealed class AcerFanTests
{
    [Fact]
    public async Task DutyWritesManualThenTheAbsoluteSpeedAndReArmsOnlyHeldFansEachTick()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new FixtureFanWmi();
        TickGate ticks = new();
        var context = Context(directory.Root);
        var service = new AcerFanService(context, new FixtureLease(new FixtureHealthEc()), () => wmi, ticks.WaitAsync);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        Assert.Equal(10, service.Descriptors.First(item => item.CapabilityId == "fan.duty").Minimum);

        var result = await service.ExecuteAsync(Duty("sys", 25), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.Equal(["SetGamingFanBehavior:000C0002", "SetGamingFanSpeed:00001902"], wmi.Writes);
        Assert.Equal(25, Value(service, "fan.duty", "sys")?.IntegerValue);
        Assert.Equal("custom", Value(service, "fan.mode", "sys")?.ChoiceValue);

        await ticks.WaitForLoopAsync();
        await ticks.AdvanceAsync();
        await ticks.AdvanceAsync();
        Assert.Equal(["SetGamingFanBehavior:000C0002", "SetGamingFanSpeed:00001902",
            "SetGamingFanBehavior:000C0002", "SetGamingFanBehavior:000C0002"], wmi.Writes);

        result = await service.ExecuteAsync(Mode("sys", "automatic"), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Applied, result.Outcome);
        Assert.True(service.HoldCompletion.IsCompleted);
        Assert.Equal("SetGamingFanBehavior:00040002", wmi.Writes[^1]);

        // Release reconciles the journaled mutation: the restore writes Auto once more, and nothing else.
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(6, wmi.Writes.Count);
        Assert.Equal("SetGamingFanBehavior:00040002", wmi.Writes[^1]);
    }

    [Fact]
    public async Task DutyBelowTheStableFloorIsRejectedWithoutAWrite()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new FixtureFanWmi();
        var context = Context(directory.Root);
        var service = new AcerFanService(context, new FixtureLease(new FixtureHealthEc()), () => wmi);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        var result = await service.ExecuteAsync(Duty("sys2", 5), context.Firmware, CancellationToken.None);
        Assert.Equal(CommandOutcome.Rejected, result.Outcome);
        Assert.Equal(CapabilityReasonCode.ValueOutOfRange, result.Reason?.Code);
        Assert.Empty(wmi.Writes);
        Assert.True(service.HoldCompletion.IsCompleted);
        await service.ReleaseAsync(cycle, CancellationToken.None);
    }

    [Fact]
    public async Task ReleaseStopsTheHoldBeforeRestoringAutoAndNothingIsWrittenAfterwards()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new FixtureFanWmi();
        TickGate ticks = new();
        var context = Context(directory.Root);
        var service = new AcerFanService(context, new FixtureLease(new FixtureHealthEc()), () => wmi, ticks.WaitAsync);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Duty("sys", 40), context.Firmware, CancellationToken.None);
        await service.ExecuteAsync(Duty("sys2", 60), context.Firmware, CancellationToken.None);
        await ticks.WaitForLoopAsync();
        await ticks.AdvanceAsync();
        Assert.Equal(["SetGamingFanBehavior:000C0002", "SetGamingFanBehavior:00300004"], wmi.Writes[^2..]);

        var release = await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(DeviceServiceState.Idle, release.State);
        Assert.True(service.HoldCompletion.IsCompleted);
        Assert.Equal(["SetGamingFanBehavior:00040002", "SetGamingFanBehavior:00100004"], wmi.Writes[^2..]);
        Assert.Equal(1, wmi.Behavior[1]);
        Assert.Equal(1, wmi.Behavior[2]);
        var count = wmi.Writes.Count;
        ticks.Tick();
        await Task.Delay(50);
        Assert.Equal(count, wmi.Writes.Count);
    }

    [Fact]
    public async Task FailedReArmKeepsAFanTheFirmwareStillReportsManualAndForgetsOneItDoesNot()
    {
        using var directory = new TemporaryDirectory();
        var wmi = new FixtureFanWmi();
        TickGate ticks = new();
        var context = Context(directory.Root);
        var service = new AcerFanService(context, new FixtureLease(new FixtureHealthEc()), () => wmi, ticks.WaitAsync);
        var cycle = Cycle(context);
        await service.AcquireAsync(cycle, CancellationToken.None);
        await service.ExecuteAsync(Duty("sys", 30), context.Firmware, CancellationToken.None);
        await ticks.WaitForLoopAsync();

        wmi.FailBehaviorWrites = true;
        await ticks.AdvanceAsync();
        await ticks.AdvanceAsync();
        Assert.Equal(4, wmi.Writes.Count);
        await service.RefreshTelemetryAsync(CancellationToken.None);
        Assert.Equal(30, Value(service, "fan.duty", "sys")?.IntegerValue);

        // The firmware no longer answers manual: the hold is dropped instead of repeated.
        wmi.Behavior[1] = 1;
        ticks.Tick();
        await service.HoldCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(5, wmi.Writes.Count);
        Assert.Equal(30, Value(service, "fan.duty", "sys")?.IntegerValue);
        await service.RefreshTelemetryAsync(CancellationToken.None);
        Assert.Null(Value(service, "fan.duty", "sys"));
        Assert.Null(Value(service, "fan.mode", "sys"));

        wmi.FailBehaviorWrites = false;
        await service.ReleaseAsync(cycle, CancellationToken.None);
        Assert.Equal(6, wmi.Writes.Count);
    }

    private static CapabilityValue? Value(AcerFanService service, string id, string instance)
    {
        return service.States.Single(state => state.CapabilityId == id && state.InstanceId == instance).ObservedValue;
    }

    private static CapabilityCommand Duty(string instance, int duty)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "fan.duty", InstanceId = instance,
            RequestedValue = CapabilityValue.Integer(duty), Deadline = Deadline.Never
        };
    }

    private static CapabilityCommand Mode(string instance, string mode)
    {
        return new CapabilityCommand
        {
            CommandId = Guid.NewGuid(), CapabilityId = "fan.mode", InstanceId = instance,
            RequestedValue = CapabilityValue.Choice(mode), Deadline = Deadline.Never
        };
    }

    private static ProfiledFeatureContext Context(string directory)
    {
        return new ProfiledFeatureContext(directory, "atlas|V1.03", new TestPluginHostAdapter(1))
        {
            Journal = TestRecovery.Open(directory)
        };
    }

    private static DeviceCycleContext<ProfiledCycleIdentity> Cycle(ProfiledFeatureContext context)
    {
        return new DeviceCycleContext<ProfiledCycleIdentity>(Deadline.Never, new ProfiledCycleIdentity(context.Firmware));
    }

    /// <summary>The measured 02CD topology: left RPM at health index 4, right at 8, CPU temperature at 1.</summary>
    private sealed class FixtureHealthEc : IAcerEcTransport
    {
        public byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null)
        {
            token.ThrowIfCancellationRequested();
            var reply = new byte[64];
            reply[1] = 0xE0;
            reply[2] = payload[2];
            if (payload[2] == 8 && payload[4] == 1)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(5), 0x02CD);
            }
            else if (payload[2] == 8 && payload[4] == 2)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(reply.AsSpan(7), payload[5] switch
                {
                    1 => 60, 4 => 1750, 8 => 1760, _ => 0
                });
            }

            return reply;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixtureLease(IAcerEcTransport ec) : IAcerLease<IAcerEcTransport>
    {
        public IAcerEcTransport Acquire()
        {
            return ec;
        }

        public ValueTask ReleaseAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Getter layouts as the firmware answers them: behavior at bit 8 + 2 x selection bit, speed at bits 8..15.</summary>
    private sealed class FixtureFanWmi : IAcerFanWmi
    {
        public Dictionary<int, int> Behavior { get; } = new() { [1] = 1, [2] = 1 };
        public Dictionary<int, int> Speed { get; } = new() { [2] = 40, [3] = 40 };
        public List<string> Writes { get; } = [];
        public bool FailBehaviorWrites { get; set; }

        public ulong Read(string method, uint input)
        {
            switch (method)
            {
                case "GetGamingFanBehavior":
                    ulong reply = 0;
                    foreach (var (bit, behavior) in Behavior)
                    {
                        reply |= (ulong)behavior << (8 + 2 * bit);
                    }

                    return reply;
                case "GetGamingFanSpeed":
                    return (ulong)Speed[(int)input] << 8;
                default:
                    throw new IOException(method);
            }
        }

        public uint Write(string method, ulong input)
        {
            lock (Writes)
            {
                Writes.Add($"{method}:{input:X8}");
            }

            switch (method)
            {
                case "SetGamingFanBehavior":
                    if (FailBehaviorWrites)
                    {
                        throw new IOException("WMI busy.");
                    }

                    for (var bit = 0; bit < 8; bit++)
                    {
                        if ((input & (1UL << bit)) != 0)
                        {
                            Behavior[bit] = (int)((input >> (16 + 2 * bit)) & 3);
                        }
                    }

                    return 0;
                case "SetGamingFanSpeed":
                    Speed[(int)(input & 0xFF)] = (int)((input >> 8) & 0xFF);
                    return 0;
                default:
                    throw new IOException(method);
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class TickGate
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();
        private readonly Channel<bool> _waiting = Channel.CreateUnbounded<bool>();

        internal async ValueTask WaitAsync(CancellationToken token)
        {
            _waiting.Writer.TryWrite(true);
            await _ticks.Reader.ReadAsync(token);
        }

        internal Task WaitForLoopAsync()
        {
            return _waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        }

        internal void Tick()
        {
            _ticks.Writer.TryWrite(true);
        }

        internal async Task AdvanceAsync()
        {
            Tick();
            await WaitForLoopAsync();
        }
    }
}
