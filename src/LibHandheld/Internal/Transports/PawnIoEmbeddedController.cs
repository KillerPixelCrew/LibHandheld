using System.Diagnostics;

namespace LibHandheld.Internal.Transports;

internal interface IEmbeddedController : IDisposable
{
    byte ReadByte(ushort address);
    ushort ReadUInt16(ushort address);
    void WriteByte(ushort address, byte value);
    void WriteUInt16(ushort address, ushort value);
    T Transaction<T>(Func<T> operation);
}

internal sealed class PawnIoEmbeddedController : IEmbeddedController
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
    private readonly ushort _dataPort;
    private readonly Lock _gate = new();
    private readonly ushort _indexPort;
    private readonly bool _indexed;
    private readonly IPawnIoModule _module;
    private bool _disposed;

    internal PawnIoEmbeddedController(IPawnIoModule module, bool indexed, ushort indexPort = 0x4E,
        ushort dataPort = 0x4F)
    {
        _module = module;
        _indexed = indexed;
        _indexPort = indexPort;
        _dataPort = dataPort;
        if (indexed && (indexPort is not (0x2E or 0x4E) || dataPort != indexPort + 1))
        {
            throw new ArgumentException(
                "Only the signed module's 0x2E/0x2F and 0x4E/0x4F SuperIO slots are supported.");
        }
    }

    public byte ReadByte(ushort address)
    {
        return Transaction(() => ReadCore(address));
    }

    public ushort ReadUInt16(ushort address)
    {
        if (address == ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        return Transaction(() => (ushort)(ReadCore(address) | (ReadCore((ushort)(address + 1)) << 8)));
    }

    public void WriteByte(ushort address, byte value)
    {
        Transaction(() =>
        {
            WriteCore(address, value);
            return true;
        });
    }

    public void WriteUInt16(ushort address, ushort value)
    {
        if (address == ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }

        Transaction(() =>
        {
            WriteCore(address, (byte)value);
            WriteCore((ushort)(address + 1), (byte)(value >> 8));
            return true;
        });
    }

    public T Transaction<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HardwareBus.Run(_indexed ? HardwareBus.IndexedIo : HardwareBus.EmbeddedController, Timeout,
                operation);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _module.Dispose();
        }
    }

    internal static PawnIoEmbeddedController OpenIndexed(ushort indexPort = 0x4E, ushort dataPort = 0x4F)
    {
        if (indexPort is not (0x2E or 0x4E) || dataPort != indexPort + 1)
        {
            throw new ArgumentException("The signed module supports only the standard SuperIO slots.");
        }

        return new PawnIoEmbeddedController(PawnIoModule.OpenModule("LpcIO"), true, indexPort, dataPort);
    }

    internal static PawnIoEmbeddedController OpenAcpi()
    {
        return new PawnIoEmbeddedController(PawnIoModule.OpenModule("LpcACPIEC"), false);
    }

    // For firmware command protocols (for example ONEXPLAYER), not an EC register read. The input
    // buffer must clear before every byte; a submitted command is never repeated after a timeout.
    internal byte[] Exchange(byte command, byte[] arguments, int responseLength)
    {
        if (_indexed)
        {
            throw new InvalidOperationException("ACPI commands cannot be sent through a SuperIO session.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(responseLength);
        return Transaction(() =>
        {
            var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
            WaitStatus(2, false, deadline);
            WritePort(0x66, command);
            foreach (var value in arguments)
            {
                WaitStatus(2, false, deadline);
                WritePort(0x62, value);
            }

            WaitStatus(2, false, deadline);
            var result = new byte[responseLength];
            for (var i = 0; i < result.Length; i++)
            {
                WaitStatus(1, true, deadline);
                result[i] = ReadPort(0x62);
            }

            return result;
        });
    }

    private byte ReadCore(ushort address)
    {
        return _indexed ? Indexed(address, false, 0) : AcpiBank(address, () => Exchange(0x80, [(byte)address], 1)[0]);
    }

    private void WriteCore(ushort address, byte value)
    {
        if (_indexed)
        {
            Indexed(address, true, value);
        }
        else
        {
            AcpiBank(address, () =>
            {
                Exchange(0x81, [(byte)address, value], 0);
                return true;
            });
        }
    }

    private T AcpiBank<T>(ushort address, Func<T> operation)
    {
        if (address <= byte.MaxValue)
        {
            return operation();
        }

        var previous = Exchange(0x80, [0xFF], 1)[0];
        Exchange(0x81, [0xFF, (byte)(address >> 8)], 0);
        try
        {
            return operation();
        }
        finally
        {
            Exchange(0x81, [0xFF, previous], 0);
        }
    }

    private byte Indexed(ushort address, bool write, byte value)
    {
        _module.Execute("ioctl_select_slot", [_indexPort == 0x2E ? 0 : 1], 0);
        WritePort(_indexPort, 0x87);
        if (_indexPort == 0x4E)
        {
            WritePort(_indexPort, 0x01);
            WritePort(_indexPort, 0x55);
            WritePort(_indexPort, 0xAA);
        }
        else
        {
            WritePort(_indexPort, 0x87);
        }

        try
        {
            SelectIndex(0x11, (byte)(address >> 8));
            SelectIndex(0x10, (byte)address);
            WritePort(_indexPort, 0x2E);
            WritePort(_dataPort, 0x12);
            WritePort(_indexPort, 0x2F);
            if (write)
            {
                WritePort(_dataPort, value);
                return value;
            }

            return ReadPort(_dataPort);
        }
        finally
        {
            // IT87's secondary 0x4E configuration space deliberately remains entered, as the signed
            // module's LHM client does. Winbond/Nuvoton/Fintek 0x2E must be explicitly exited.
            if (_indexPort == 0x2E)
            {
                WritePort(_indexPort, 0xAA);
            }
        }
    }

    private void SelectIndex(byte selector, byte value)
    {
        WritePort(_indexPort, 0x2E);
        WritePort(_dataPort, selector);
        WritePort(_indexPort, 0x2F);
        WritePort(_dataPort, value);
    }

    private byte ReadPort(ushort port)
    {
        var function = _indexed ? "ioctl_pio_inb" : "ioctl_pio_read";
        return checked((byte)_module.Execute(function, [port], 1)[0]);
    }

    private void WritePort(ushort port, byte value)
    {
        _module.Execute(_indexed ? "ioctl_pio_outb" : "ioctl_pio_write", [port, value], 0);
    }

    private void WaitStatus(byte mask, bool set, long deadline)
    {
        while ((ReadPort(0x66) & mask) != 0 != set)
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The embedded controller did not acknowledge the transaction.");
            }

            Thread.Sleep(1);
        }
    }
}
