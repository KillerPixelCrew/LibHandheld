using System.Globalization;
using System.Management;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.OneXPlayer;

/// <summary>X2's WMI register provider. Bank precedes offset in its packed method argument.</summary>
internal sealed class OneXWmiEc : IEmbeddedController
{
    private readonly Lock _gate = new();
    private readonly ManagementObject _instance;
    private bool _disposed;

    internal OneXWmiEc()
    {
        using var query = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM SuRwECRegInterface");
        using var result = query.Get();
        _instance = result.Cast<ManagementObject>().FirstOrDefault()
                    ?? throw new InvalidOperationException("ONEXPLAYER SuRwECRegInterface is unavailable.");
    }

    public byte ReadByte(ushort address)
    {
        return Transaction(() =>
        {
            object?[] arguments = [Address(address), null];
            _instance.InvokeMethod("ReadECReg", arguments);
            var fields = Response(arguments);
            if (fields.Length < 2)
            {
                throw new InvalidDataException("The EC read response is incomplete.");
            }

            return Hex(fields[1]);
        });
    }

    public ushort ReadUInt16(ushort address)
    {
        return Transaction(() =>
            (ushort)(ReadByte(address) | (ReadByte(checked((ushort)(address + 1))) << 8)));
    }

    public void WriteByte(ushort address, byte value)
    {
        Transaction(() =>
        {
            object?[] arguments = [Address(address) | ((uint)value << 16), null];
            _instance.InvokeMethod("WriteECReg", arguments);
            _ = Response(arguments);
            return true;
        });
    }

    public void WriteUInt16(ushort address, ushort value)
    {
        Transaction(() =>
        {
            WriteByte(address, (byte)value);
            WriteByte(checked((ushort)(address + 1)), (byte)(value >> 8));
            return true;
        });
    }

    public T Transaction<T>(Func<T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return HardwareBus.Run(@"Global\Access_EC", TimeSpan.FromMilliseconds(500), operation);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _instance.Dispose();
        }
    }

    internal static uint Address(ushort register)
    {
        return (uint)(register >> 8) | ((uint)(byte)register << 8);
    }

    private static string[] Response(object?[] arguments)
    {
        if (arguments[1] is not string response)
        {
            throw new InvalidDataException("The EC provider returned no response.");
        }

        var fields = response.Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length == 0 || Hex(fields[0]) != 0)
        {
            throw new IOException("The EC provider rejected the register operation.");
        }

        return fields;
    }

    private static byte Hex(string value)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return byte.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
