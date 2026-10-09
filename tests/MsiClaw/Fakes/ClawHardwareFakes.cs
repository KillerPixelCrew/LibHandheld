using System.Buffers.Binary;

namespace LibHandheld.Families.MsiClaw.Tests.Fakes;

internal sealed class FakeIdentityReader : IClawIdentityReader
{
    public int ReadCount { get; private set; }

    /// <summary>The model this machine reads as; a test may change it between reads.</summary>
    public ClawModel Model { get; set; } = ClawModels.Claw8A2Vm;

    /// <summary>The MCU revision the snapshot reports, when a test needs another one.</summary>
    public string? McuRevision { get; init; }

    /// <summary>The power source each command reads.</summary>
    public bool OnAcPower { get; set; } = true;

    /// <summary>How often a command read the power source.</summary>
    public int AcPowerReads { get; private set; }

    public bool ReadOnAcPower()
    {
        AcPowerReads++;
        return OnAcPower;
    }

    public ValueTask<ClawIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        var state = CreateState();
        return ValueTask.FromResult(state with
        {
            Model = Model,
            Snapshot = state.Snapshot with
            {
                BaseboardProduct = Model.BoardProduct,
                McuFirmwareVersion = McuRevision ?? state.Snapshot.McuFirmwareVersion
            }
        });
    }

    public static ClawIdentityState CreateState()
    {
        return new ClawIdentityState
        {
            Snapshot = new DeviceIdentitySnapshot
            {
                SystemManufacturer = ClawHardwareFacts.Manufacturer,
                BaseboardManufacturer = ClawHardwareFacts.Manufacturer,
                BaseboardProduct = ClawModels.Claw8A2Vm.BoardProduct,
                SystemSku = "1T52.1",
                BiosVersion = "E1T52IMS.114",
                EcFirmwareVersion = "1T52EMS1.1091204202509:10:47",
                // A revision the plugin was never reviewed against, on purpose: controller and
                // lighting ownership must not depend on it.
                McuFirmwareVersion = "0230",
                UsbEndpoints =
                [
                    new UsbEndpointObservation
                    {
                        VendorId = ClawHardwareFacts.Hex(ClawHardwareFacts.UsbVendorId),
                        ProductId = ClawHardwareFacts.Hex(ClawHardwareFacts.XInputProductId),
                        DeviceRelease = "0230"
                    }
                ]
            },
            ExactMachineMatch = true,
            Model = ClawModels.Claw8A2Vm,
            WmiAvailable = true,
            OnAcPower = true
        };
    }
}

internal sealed class FakeWmiTransport : IMsiWmiTransport
{
    private readonly Dictionary<(string Method, byte Selector), byte[]> _responses = [];

    public FakeWmiTransport()
    {
        SetData(ClawHardwareFacts.PowerSustainedAddress, 30);
        SetData(ClawHardwareFacts.PowerBoostAddress, 37);
        SetData(ClawHardwareFacts.ScenarioAddress, 0xC1);
        SetData(ClawHardwareFacts.FanCustomAddress, 0);
        SetData(ClawHardwareFacts.FanFullSpeedAddress, 2);
        SetData(ClawHardwareFacts.ChargeLimitAddress, 0x80 | 80);
        _responses[("Get_Fan", 0)] = Response(0, 0xC7, 0, 0xCF);
        _responses[("Get_Temperature", 0)] = Response(52);
        _responses[("Get_Fan", 1)] = Table(0xA1, 0, 40, 49, 58, 67, 75, 0xA8);
        _responses[("Get_Fan", 2)] = Table(0x91, 0, 40, 49, 58, 67, 75, 0x98);
        _responses[("Get_Temperature", 1)] = Table(0, 0xB2, 0xB3, 50, 60, 70, 80, 88);
        _responses[("Get_Temperature", 2)] = Table(0, 0xC2, 0xC3, 50, 60, 70, 80, 88);
    }

    public List<(string Method, byte[] Package)> Writes { get; } = [];

    public int Reads { get; private set; }

    public bool FailNextSetter { get; set; }

    public bool GetterTimeout { get; set; }

    public bool SetterTimeout { get; set; }

    public Action<string, byte[]>? AfterSetter { get; set; }

    public int ProviderAvailabilityChecks { get; private set; }

    public ValueTask<bool> IsProviderAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProviderAvailabilityChecks++;
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Reads++;
        if (GetterTimeout)
        {
            throw new OperationCanceledException("Synthetic WMI transport timeout.");
        }

        return ValueTask.FromResult((byte[])[.. _responses[(methodName, selector)]]);
    }

    public ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SetterTimeout)
        {
            throw new OperationCanceledException("Synthetic WMI transport timeout.");
        }

        if (FailNextSetter)
        {
            FailNextSetter = false;
            throw new IOException("Synthetic WMI setter failure.");
        }

        Writes.Add((methodName, [.. package]));
        var selector = package[0];
        if (methodName == "Set_Data")
        {
            _responses[("Get_Data", selector)] = Response(package[1], package[2], package[3], package[4]);
            SyncAccessPoints(selector, package[1]);
        }
        else
        {
            var getter = methodName == "Set_Fan" ? "Get_Fan" : "Get_Temperature";
            byte[] response = [.. package];
            response[0] = 1;
            _responses[(getter, selector)] = response;
        }

        AfterSetter?.Invoke(methodName, package);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void SetResponse(string method, byte selector, byte[] response)
    {
        _responses[(method, selector)] = response;
    }

    /// <summary>The EC byte at an address, as HC reads it: the first byte after the status.</summary>
    public int ReadData(byte address)
    {
        return _responses[("Get_Data", address)][1];
    }

    /// <summary>
    ///     Sets an EC byte. The SHIFT byte (0xD2) and the custom-fan byte (0xD4) are also what HC reads
    ///     through <c>Get_AP</c> blocks 0 and 1, so those answers follow.
    /// </summary>
    public void SetData(byte address, int value)
    {
        _responses[("Get_Data", address)] = Data(value);
        SyncAccessPoints(address, (byte)value);
    }

    private void SyncAccessPoints(byte address, byte value)
    {
        if (address == ClawHardwareFacts.ScenarioAddress)
        {
            var block = Response();
            block[3] = value;
            _responses[("Get_AP", 0)] = block;
        }
        else if (address == ClawHardwareFacts.FanCustomAddress)
        {
            _responses[("Get_AP", 1)] = Response(value);
        }
    }

    private static byte[] Data(int value)
    {
        var response = new byte[32];
        response[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(1, sizeof(int)), value);
        return response;
    }

    private static byte[] Response(params byte[] payload)
    {
        var response = new byte[32];
        response[0] = 1;
        payload.CopyTo(response, 1);
        return response;
    }

    private static byte[] Table(params byte[] payload)
    {
        return Response(payload);
    }
}

internal sealed class FakeOemEventSource : IMsiOemEventSource
{
    private Func<byte, DateTimeOffset, ValueTask>? _callback;

    /// <summary>How often the plugin asked for the MSI_Event repair.</summary>
    public int RepairRequests { get; private set; }

    public ValueTask EnsureEventClassAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RepairRequests++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = callback;
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _callback = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask EmitAsync(byte code, DateTimeOffset timestamp)
    {
        return _callback?.Invoke(code, timestamp) ?? ValueTask.CompletedTask;
    }
}

internal sealed class FakeMcuTransport : IClawMcuTransport
{
    private byte[] _profile = ClawLightingCapability.Encode(new LightingState(50, 0, 0, 0));

    public Action? AfterNextWrite { get; set; }

    public Func<byte[], byte[]>? TransformNextWrite { get; set; }

    public List<byte[]> ProfileWrites { get; } = [];

    public List<ushort> ReadAddresses { get; } = [];

    /// <summary>Every write, lighting and paddle mapping alike, with the address it went to.</summary>
    public List<(ushort Address, byte[] Payload)> Writes { get; } = [];

    public int RomSyncs { get; private set; }

    public List<ClawControllerMode> ModeSwitches { get; } = [];

    /// <summary>When set, the MCU answers profile reads with this exception.</summary>
    public Exception? ReadFailure { get; set; }

    public byte[] Profile
    {
        get => [.. _profile];
        init => _profile = [.. value];
    }

    public ValueTask SyncToRomAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RomSyncs++;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadAddresses.Add(address);
        return ReadFailure is { } failure
            ? ValueTask.FromException<byte[]>(failure)
            : ValueTask.FromResult((byte[])[.. _profile]);
    }

    public ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var write = payload.ToArray();
        Writes.Add((address, [.. write]));
        if (write.Length != 32)
        {
            return ValueTask.CompletedTask;
        }

        ProfileWrites.Add([.. write]);
        var transform = TransformNextWrite;
        TransformNextWrite = null;
        _profile = transform is null ? write : transform([.. write]);
        var afterWrite = AfterNextWrite;
        AfterNextWrite = null;
        afterWrite?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModeSwitches.Add(mode);
        return ValueTask.FromResult(new ControllerTopology(
            mode,
            mode == ClawControllerMode.XInput
                ? ClawHardwareFacts.XInputProductId
                : ClawHardwareFacts.DirectInputProductId,
            physicalLocation,
            FakeControllerSource.Nodes));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeControllerSource : IClawControllerSource
{
    /// <summary>The pad's one hideable node, as discovery reports it on the reference unit.</summary>
    internal static readonly IReadOnlyList<PhysicalDeviceIdentity> Nodes =
    [
        new()
        {
            InstancePath = @"HID\VID_0DB0&PID_1901&IG_00\8&FAKE&0&0000",
            LocationPath = "PCIROOT(0)#USBROOT(0)#USB(2)",
            RequiresHiding = true
        }
    ];

    private readonly Lock _gate = new();
    private Task? _activePublication;
    private Func<CanonicalControllerSample, CancellationToken, ValueTask>? _publish;
    private CancellationTokenSource? _readerCancellation;

    public ControllerTopology Topology { get; init; } = new(
        ClawControllerMode.XInput,
        ClawHardwareFacts.XInputProductId,
        "PCIROOT(0)#USBROOT(0)#USB(2)",
        Nodes);

    public bool FailNextRumble { get; set; }

    public bool FailStop { get; init; }

    public int RumbleWriteAttempts { get; private set; }

    public List<(byte Weak, byte Strong)> RumbleWrites { get; } = [];

    public ClawModel? Model { get; private set; }

    public ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ControllerTopology?>(Topology);
    }

    public ValueTask StartAsync(
        ClawModel model,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Model = model;
        lock (_gate)
        {
            _readerCancellation?.Dispose();
            _readerCancellation = new CancellationTokenSource();
            _publish = publish;
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancellationTokenSource? readerCancellation;
        Task? activePublication;
        lock (_gate)
        {
            readerCancellation = _readerCancellation;
            activePublication = _activePublication;
            readerCancellation?.Cancel();
        }

        if (activePublication is not null)
        {
            try
            {
                await activePublication.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (readerCancellation?.IsCancellationRequested == true)
            {
            }
        }

        lock (_gate)
        {
            _publish = null;
            _activePublication = null;
            _readerCancellation?.Dispose();
            _readerCancellation = null;
        }

        if (FailStop)
        {
            throw new IOException("Synthetic controller source stop failure.");
        }
    }

    public ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RumbleWriteAttempts++;
        if (!FailNextRumble)
        {
            RumbleWrites.Add((weak, strong));
            return ValueTask.CompletedTask;
        }

        FailNextRumble = false;
        throw new IOException("Synthetic rumble write failure.");
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask EmitAsync(CanonicalControllerSample sample)
    {
        lock (_gate)
        {
            var publish = _publish ?? throw new InvalidOperationException("The fake reader is not active.");
            var cancellationToken = _readerCancellation?.Token
                                    ?? throw new InvalidOperationException(
                                        "The fake reader has no cancellation source.");
            _activePublication = publish(sample, cancellationToken).AsTask();
            return new ValueTask(_activePublication);
        }
    }
}

/// <summary>
///     A motion source that accepts every start and keeps the publish callback, so a test can
///     drive samples through the service.
/// </summary>
internal sealed class FakeMotionSource : IClawMotionSource
{
    public Action<MotionSample>? Publish { get; private set; }

    /// <summary>Whether the source is between a start and the following stop.</summary>
    public bool Started { get; private set; }

    /// <summary>How many times the source was started.</summary>
    public int StartCount { get; private set; }

    /// <summary>The model the last start named.</summary>
    public ClawModel? Model { get; private set; }

    public ValueTask<bool> StartAsync(
        ClawModel model,
        Action<MotionSample> publish,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Model = model;
        Publish = publish;
        Started = true;
        StartCount++;
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Started = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeChordSuppressor : IFirmwareChordSuppressor
{
    private Action<FirmwareChord>? _chord;
    private Action<Exception>? _fault;

    public ValueTask<bool> StartAsync(
        Action<Exception> fault,
        Action<FirmwareChord> chord,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fault = fault;
        _chord = chord;
        return ValueTask.FromResult(true);
    }

    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fault = null;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public void TriggerChord(FirmwareChord chord)
    {
        (_chord ?? throw new InvalidOperationException("The fake hook is not active."))(chord);
    }

    public void TriggerFault(Exception exception)
    {
        (_fault ?? throw new InvalidOperationException("The fake hook is not active."))(exception);
    }
}
