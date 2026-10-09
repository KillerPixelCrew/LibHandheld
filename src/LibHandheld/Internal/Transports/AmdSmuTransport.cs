using System.Diagnostics;

namespace LibHandheld.Internal.Transports;

internal sealed class SmuCommandException(uint command, uint status)
    : IOException($"AMD SMU command 0x{command:X} was rejected with status 0x{status:X}.")
{
    internal uint Status { get; } = status;
}

internal sealed class AmdSmuTransport : ICpuPowerTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private readonly Lock _gate = new();
    private readonly IPawnIoModule _module;
    private bool _disposed;
    private SmuMailbox _mailbox;
    private bool _moduleMailbox;

    internal AmdSmuTransport(IPawnIoModule module, uint codeName, uint firmwareVersion)
    {
        _module = module;
        CodeName = codeName;
        FirmwareVersion = firmwareVersion;
        Commands = CommandsFor(codeName) ??
                   throw new NotSupportedException($"AMD codename {codeName} has no documented power commands.");
        _mailbox = MailboxFor(codeName);
    }

    internal uint CodeName { get; }
    internal uint FirmwareVersion { get; }
    internal SmuPowerCommands Commands { get; }

    public CpuPowerLimits ReadLimits()
    {
        // These IDs are SET messages. Sending zero is a write, never a read of the current limit.
        // No reviewed read protocol is available here; callers retain accepted writes as observations.
        throw new NotSupportedException("AMD power-limit readback is unavailable for this transport.");
    }

    public void WriteLimits(CpuPowerLimits limits)
    {
        Validate(limits);
        // Validate the complete request before the first write. These are independent firmware
        // commands; if one fails the caller sees the failure and no uncertain command is retried.
        Transaction(() =>
        {
            SendCore(Commands.Sustained, [CpuPowerEncoding.Milliwatts(limits.SustainedWatts)]);
            SendCore(Commands.Boost, [CpuPowerEncoding.Milliwatts(limits.BoostWatts)]);
            SendCore(Commands.Slow, [CpuPowerEncoding.Milliwatts(limits.SlowWatts)]);
            return true;
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _module.Dispose();
        }
    }

    internal static AmdSmuTransport Open()
    {
        var module = PawnIoModule.OpenRyzen();
        try
        {
            var transport = HardwareBus.Run(HardwareBus.Pci, Timeout, () => new AmdSmuTransport(module,
                checked((uint)module.Execute("ioctl_get_code_name", [], 1)[0]),
                checked((uint)module.Execute("ioctl_get_smu_version", [], 1)[0])));
            transport.SelectMailbox();
            return transport;
        }
        catch
        {
            module.Dispose();
            throw;
        }
    }

    internal void WriteLimit(CpuPowerLimit limit, double watts)
    {
        var value = CpuPowerEncoding.Milliwatts(watts);
        var command = limit switch
        {
            CpuPowerLimit.Sustained => Commands.Sustained,
            CpuPowerLimit.Slow => Commands.Slow,
            CpuPowerLimit.Boost => Commands.Boost,
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        Send(command, [value]);
    }

    internal uint[] Send(uint command, uint[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (command == 0 || arguments.Length > 6)
        {
            throw new ArgumentException("SMU commands require an ID and at most six arguments.");
        }

        return Transaction(() => SendCore(command, arguments));
    }

    internal void SetGpuClock(int megahertz)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(megahertz);
        if (CodeName is 2 or 6 or 7 or 15)
        {
            // Older Raven/Picasso parts expose a min/max pair rather than the fixed-clock command.
            Transaction(() =>
            {
                SendModuleCore(0x47, [(uint)megahertz]);
                SendModuleCore(0x46, [(uint)megahertz]);
                return true;
            });
            return;
        }

        Transaction(() =>
        {
            SendModuleCore(0x89, [(uint)megahertz]);
            return true;
        });
    }

    private void SelectMailbox()
    {
        Transaction(() =>
        {
            try
            {
                SendCore(1, [0x47]);
            }
            catch (Exception failure) when (failure is SmuCommandException or TimeoutException)
            {
                // This is the read-only mailbox version query, never a power setter. Its timeout
                // permits testing an independent mailbox; user writes never enter this fallback.
                _mailbox = PsmuMailboxFor(CodeName);
                try
                {
                    SendCore(1, [0x47]);
                }
                catch (Exception alternate) when (alternate is SmuCommandException or TimeoutException)
                {
                    SendModuleCore(1, [0x47]);
                    _moduleMailbox = true;
                }
            }

            return true;
        });
    }

    private uint[] SendModuleCore(uint command, uint[] arguments)
    {
        var input = new long[7];
        input[0] = command;
        for (var i = 0; i < arguments.Length; i++)
        {
            input[i + 1] = arguments[i];
        }

        return _module.Execute("ioctl_send_smu_command", input, 6).Select(value => checked((uint)value)).ToArray();
    }

    private uint[] SendCore(uint command, uint[] arguments)
    {
        if (_moduleMailbox)
        {
            return SendModuleCore(command, arguments);
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(Timeout.TotalSeconds * Stopwatch.Frequency);
        WaitResponse(deadline);
        WriteRegister(_mailbox.Response, 0);
        for (var i = 0; i < 6; i++)
        {
            WriteRegister(_mailbox.Arguments + (uint)(i * 4), i < arguments.Length ? arguments[i] : 0);
        }

        WriteRegister(_mailbox.Command, command);
        var status = WaitResponse(deadline);
        if (status != 1)
        {
            throw new SmuCommandException(command, status);
        }

        var response = new uint[6];
        for (var i = 0; i < response.Length; i++)
        {
            response[i] = ReadRegister(_mailbox.Arguments + (uint)(i * 4));
        }

        return response;
    }

    private uint WaitResponse(long deadline)
    {
        while (true)
        {
            var value = ReadRegister(_mailbox.Response);
            if (value != 0)
            {
                return value;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("AMD SMU response stayed zero; command completion is unknown.");
            }

            Thread.Sleep(1);
        }
    }

    private uint ReadRegister(uint address)
    {
        return checked((uint)_module.Execute("ioctl_read_smu_register", [address], 1)[0]);
    }

    private void WriteRegister(uint address, uint value)
    {
        _module.Execute("ioctl_write_smu_register", [address, value], 0);
    }

    private T Transaction<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HardwareBus.Run(HardwareBus.Pci, Timeout, operation);
        }
    }

    internal static SmuPowerCommands? CommandsFor(uint codeName)
    {
        return codeName switch
        {
            2 or 6 or 7 or 15 => new SmuPowerCommands(0x1A, 0x1C, 0x1B),
            1 or 10 or 12 or 13 or 22 or 23 or 24 or 25 or 30 or 31 or 32 or 33 => new SmuPowerCommands(0x14,
                0x16, 0x15),
            16 or 17 or 28 => new SmuPowerCommands(0x4F, 0x5F, 0x3E),
            _ => null
        };
    }

    internal static SmuMailbox MailboxFor(uint codeName)
    {
        return codeName switch
        {
            10 or 12 or 23 or 24 or 25 or 30 => new SmuMailbox(0x03B10528, 0x03B10578, 0x03B10998),
            31 or 32 or 33 or 34 or 38 => new SmuMailbox(0x03B10928, 0x03B10978, 0x03B10998),
            28 => new SmuMailbox(0x03B10530, 0x03B1057C, 0x03B109C4),
            _ => new SmuMailbox(0x03B10528, 0x03B10564, 0x03B10998)
        };
    }

    internal static SmuMailbox PsmuMailboxFor(uint codeName)
    {
        return codeName switch
        {
            28 => new SmuMailbox(0x03B10524, 0x03B10570, 0x03B10A40),
            38 => new SmuMailbox(0x03B10924, 0x03B10970, 0x03B10A40),
            _ => new SmuMailbox(0x03B10A20, 0x03B10A80, 0x03B10A88)
        };
    }

    private static void Validate(CpuPowerLimits limits)
    {
        CpuPowerEncoding.Milliwatts(limits.SustainedWatts);
        CpuPowerEncoding.Milliwatts(limits.SlowWatts);
        CpuPowerEncoding.Milliwatts(limits.BoostWatts);
    }
}
