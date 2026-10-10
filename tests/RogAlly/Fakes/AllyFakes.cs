using System.Buffers.Binary;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly.Tests.Fakes;

/// <summary>An ATKACPI firmware model: scalars and curves that reads return and writes change.</summary>
internal sealed class FakeAsusAcpi : IAsusAcpi
{
    private readonly Dictionary<AsusAcpiId, byte[]> _curves = new()
    {
        [AsusAcpiId.CpuFanCurve] = [.. AllyFanCapability.DefaultCpuCurve],
        [AsusAcpiId.GpuFanCurve] = [.. AllyFanCapability.DefaultGpuCurve]
    };

    private readonly Dictionary<AsusAcpiId, int> _scalars = new()
    {
        [AsusAcpiId.PerformanceMode] = 0,
        [AsusAcpiId.SustainedPower] = 15,
        [AsusAcpiId.SlowPower] = 20,
        [AsusAcpiId.FastPower] = 25,
        [AsusAcpiId.ChargeLimit] = 100,
        [AsusAcpiId.CpuFanSpeed] = 30,
        [AsusAcpiId.GpuFanSpeed] = 28
    };

    public bool Available { get; set; } = true;

    /// <summary>When false, DSTS reports every scalar as unsupported, as a firmware with write-only limits would.</summary>
    public bool ScalarsReadable { get; set; } = true;

    /// <summary>When set, writes to this ID are accepted but ignored.</summary>
    public AsusAcpiId? IgnoreWritesTo { get; set; }

    /// <summary>When set, a write to this ID throws after it lands.</summary>
    public AsusAcpiId? FailWritesTo { get; set; }

    public Exception? WriteFailure { get; set; }

    public Action<AsusAcpiId>? AfterWrite { get; set; }

    public int StatusReads { get; private set; }

    public int ControlStatusReads { get; private set; }

    public int BufferReads { get; private set; }

    public List<(AsusAcpiId Id, uint Value)> Writes { get; } = [];

    public List<(AsusAcpiId Id, byte[] Data)> BufferWrites { get; } = [];

    public bool TryOpen()
    {
        return Available;
    }

    public uint ReadStatus(AsusAcpiId id, uint selector = 0)
    {
        StatusReads++;
        if (id is not (AsusAcpiId.CpuFanSpeed or AsusAcpiId.GpuFanSpeed))
        {
            ControlStatusReads++;
        }

        if (AsusAcpiProtocol.IsCurve(id))
        {
            return _curves.TryGetValue(id, out var curve)
                ? BinaryPrimitives.ReadUInt32LittleEndian(curve)
                : AsusAcpiProtocol.Unsupported;
        }

        return ScalarsReadable && _scalars.TryGetValue(id, out var value)
            ? 0x10000u | (uint)value
            : AsusAcpiProtocol.Unsupported;
    }

    public byte[] ReadBuffer(AsusAcpiId id, uint selector)
    {
        BufferReads++;
        var response = new byte[16];
        if (_curves.TryGetValue(id, out var curve))
        {
            curve.CopyTo(response, 0);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(response, AsusAcpiProtocol.Unsupported);
        }

        return response;
    }

    public uint Write(AsusAcpiId id, uint value)
    {
        Writes.Add((id, value));
        if (IgnoreWritesTo != id)
        {
            _scalars[id] = (int)value;
        }

        AfterWrite?.Invoke(id);
        return FailWritesTo == id ? throw WriteFailure ?? new IOException("simulated ATKACPI failure") : 1u;
    }

    public uint WriteBuffer(AsusAcpiId id, ReadOnlySpan<byte> data)
    {
        BufferWrites.Add((id, data.ToArray()));
        if (IgnoreWritesTo != id)
        {
            _curves[id] = data.ToArray();
        }

        AfterWrite?.Invoke(id);
        return FailWritesTo == id ? throw WriteFailure ?? new IOException("simulated ATKACPI failure") : 1u;
    }

    public void Dispose()
    {
    }

    public int Scalar(AsusAcpiId id)
    {
        return _scalars[id];
    }

    public void SetScalar(AsusAcpiId id, int value)
    {
        _scalars[id] = value;
    }

    public byte[] Curve(AsusAcpiId id)
    {
        return _curves[id];
    }

    public void SetCurve(AsusAcpiId id, byte[] curve)
    {
        _curves[id] = curve;
    }
}

internal sealed class FakeIdentityReader(string product, string manufacturer = AllyModels.Manufacturer)
    : IAllyIdentityReader
{
    public string Product { get; set; } = product;
    public int Reads { get; private set; }

    public ValueTask<AllyIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        var snapshot = new DeviceIdentitySnapshot
        {
            BaseboardManufacturer = manufacturer,
            BaseboardProduct = Product,
            BiosVersion = "RC72LA.312"
        };
        return ValueTask.FromResult(new AllyIdentityState
        {
            Snapshot = snapshot,
            Model = AllyModels.Match(snapshot),
            OnAcPower = true
        });
    }
}

internal sealed class FakeVendorHid : IAllyVendorHid
{
    private Func<byte, DateTimeOffset, ValueTask>? _callback;
    private Action<Exception>? _fault;

    public bool Available { get; set; } = true;
    public bool Reading => _callback is not null;
    public int ReaderStarts { get; private set; }

    public bool FailWrites { get; set; }

    /// <summary>A table (report byte 3) the fake refuses while accepting every other.</summary>
    public byte? RefuseTable { get; set; }

    public List<byte[]> Reports { get; } = [];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Available);
    }

    public ValueTask<bool> StartAsync(Func<byte, DateTimeOffset, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        _callback = Available ? callback : null;
        _fault = Available ? fault : null;
        if (Available)
        {
            ReaderStarts++;
        }

        return ValueTask.FromResult(Available);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _callback = null;
        _fault = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteConfigurationAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        if (FailWrites || (RefuseTable is { } table && report.Span[2] == 0x02 && report.Span[3] == table))
        {
            throw new IOException("simulated feature report failure");
        }

        Reports.Add(report.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the reader the way a failed read does.</summary>
    public void RaiseFault()
    {
        var fault = _fault;
        _callback = null;
        _fault = null;
        fault?.Invoke(new IOException("simulated vendor read failure"));
    }

    public ValueTask RaiseAsync(byte code)
    {
        return _callback?.Invoke(code, DateTimeOffset.UtcNow) ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeAuraHid : IAllyAuraHid
{
    public bool Available { get; set; } = true;

    public int DynamicLightingRequests { get; private set; }

    public List<(byte[] Report, bool Feature)> Reports { get; } = [];

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Available);
    }

    public ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        Reports.Add((report.ToArray(), true));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        Reports.Add((report.ToArray(), false));
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DisableDynamicLightingAsync(CancellationToken cancellationToken)
    {
        DynamicLightingRequests++;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeControllerSource : IAllyControllerSource
{
    private Action<Exception>? _fault;
    private Func<CanonicalControllerSample, CancellationToken, ValueTask>? _publish;

    /// <summary>How many times the reader was started.</summary>
    public int Starts { get; private set; }

    public bool Present { get; set; } = true;

    public IReadOnlyList<PhysicalDeviceIdentity> Devices { get; set; } =
    [
        new()
        {
            InstancePath = @"USB\VID_0B05&PID_1B4C&MI_00\7&1234&0&0000",
            VendorId = "0B05",
            ProductId = "1B4C",
            RequiresHiding = true
        }
    ];

    public bool Running { get; private set; }

    public bool FailRumble { get; set; }

    /// <summary>The OEM button state the plugin hands the real source, merged the same way.</summary>
    public AllyOemButtonState? Buttons { get; set; }

    public List<(float Low, float High)> Rumble { get; } = [];

    public ValueTask<AllyControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        // As the real source: only a pad whose slot answers and whose nodes are present counts.
        return ValueTask.FromResult(Present && Devices.Count > 0
            ? new AllyControllerTopology(0, Devices, "fake")
            : null);
    }

    public ValueTask StartAsync(AllyControllerTopology topology,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        if (Running)
        {
            throw new InvalidOperationException("The Ally controller reader is already active.");
        }

        _publish = publish;
        _fault = fault;
        Running = true;
        Starts++;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Running = false;
        _publish = null;
        _fault = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteRumbleAsync(float low, float high, CancellationToken cancellationToken)
    {
        if (!Running)
        {
            throw new InvalidOperationException("The Ally controller route is no longer available for rumble.");
        }

        Rumble.Add((low, high));
        if (FailRumble)
        {
            throw new IOException("simulated rumble output failure");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Stops the reader the way a transient XInput failure does; the worker stays registered.</summary>
    public void RaiseFault()
    {
        var fault = _fault;
        _publish = null;
        fault?.Invoke(new InvalidOperationException("simulated XInput slot failure"));
    }

    /// <summary>Stops the reader the way the pad dropping off the bus does.</summary>
    public void RaiseLost()
    {
        var fault = _fault;
        _publish = null;
        fault?.Invoke(new IOException("simulated XInput slot 0 stopped answering (1167)."));
    }

    public ValueTask EmitAsync(CanonicalButtons buttons)
    {
        var now = DateTimeOffset.UtcNow;
        return _publish?.Invoke(new CanonicalControllerSample
        {
            Timestamp = now,
            Buttons = buttons | (Buttons?.Current(now) ?? CanonicalButtons.None)
        }, CancellationToken.None) ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeMotionSource : IAllyMotionSource
{
    public bool Present { get; set; } = true;

    public ValueTask<bool> StartAsync(Action<MotionSample> publish, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(Present);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeKeyboardSource : IAllyKeyboardSource
{
    private Func<AllyKeyEvent, ValueTask>? _callback;

    public IReadOnlyCollection<uint> Watched { get; private set; } = [];

    /// <summary>Whether the hook is installed.</summary>
    public bool Hooked => _callback is not null;

    public ValueTask<bool> StartAsync(Func<AllyKeyEvent, ValueTask> callback, Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        _callback = callback;
        return ValueTask.FromResult(true);
    }

    public void Watch(IReadOnlyCollection<uint> virtualKeys)
    {
        Watched = [.. virtualKeys];
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        _callback = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>Delivers a key the way the hook would: only when it is watched.</summary>
    public ValueTask PressAsync(uint virtualKey, bool down)
    {
        return Watched.Contains(virtualKey) && _callback is not null
            ? _callback(new AllyKeyEvent(virtualKey, down, DateTimeOffset.UtcNow))
            : ValueTask.CompletedTask;
    }
}

/// <summary>One set of fakes wired into a plugin.</summary>
internal sealed class AllyFakeHardware(string product = "RC72LA")
{
    /// <summary>Skips HC's and HHD's write spacing so the tests do not sleep through it.</summary>
    public static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = static (_, _) => Task.CompletedTask;

    public FakeAsusAcpi Acpi { get; } = new();

    public FakeAuraHid Aura { get; } = new();

    public FakeControllerSource Controller { get; } = new();

    public FakeIdentityReader Identity { get; } = new(product);

    public FakeKeyboardSource Keyboard { get; } = new();

    public FakeMotionSource Motion { get; } = new();

    public FakeVendorHid Vendor { get; } = new();

    public AllyDeviceEngine? Provider { get; private set; }

    public ServiceEngineFixture CreatePlugin()
    {
        return new ServiceEngineFixture(context => Provider = new AllyDeviceEngine(context, new AllyHardwareServices(
            Identity,
            Acpi,
            _ => Vendor,
            _ => Aura,
            (_, buttons) =>
            {
                Controller.Buttons = buttons;
                return Controller;
            },
            _ => Motion,
            Keyboard,
            NoDelay,
            () => true)));
    }
}
