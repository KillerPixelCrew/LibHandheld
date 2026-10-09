using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Zotac;

/// <summary>Pure command construction for the HC Zone 65-byte vendor collection.</summary>
internal static class ZotacProtocol
{
    internal static byte[] Command(byte command, byte argument = 0, byte value = 0)
    {
        var report = new byte[65];
        report[1] = 0xE1;
        report[4] = 60;
        report[5] = command;
        report[6] = argument;
        report[7] = value;
        Seal(report);
        return report;
    }

    internal static byte[] Rear(byte button)
    {
        var report = Command(0xA1, button);
        report[11] = 0x09;
        report[13] = button == 1 ? (byte)68 : (byte)69;
        Seal(report);
        return report;
    }

    internal static byte[] Color(byte zone, int rgb)
    {
        var report = Command(0xAD, 0, zone);
        for (var index = 0; index < 10; index++)
        {
            var offset = 10 + index * 3;
            report[offset] = (byte)(rgb >> 16);
            report[offset + 1] = (byte)(rgb >> 8);
            report[offset + 2] = (byte)rgb;
        }

        Seal(report);
        return report;
    }

    internal static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            var first = (uint)((crc ^ value) & 0xFF);
            var nibble = first & 15;
            var folded = (nibble << 4) ^ first;
            var high = folded >> 4;
            var intermediate = (((folded << 1) ^ high) << 4) ^ nibble;
            crc = (ushort)(((intermediate << 3) ^ high ^ ((uint)crc >> 8)) & 0xFFFF);
        }

        return crc;
    }

    private static void Seal(byte[] report)
    {
        var crc = Crc(report.AsSpan(5, 58));
        report[63] = (byte)(crc >> 8);
        report[64] = (byte)crc;
    }
}

internal sealed class ZotacCommandTransport : IDisposable
{
    private readonly HidCollection _collection;
    private readonly SafeFileHandle _handle;

    private ZotacCommandTransport(HidCollection collection)
    {
        _collection = collection;
        _handle = HidDevices.Open(collection, false);
    }

    internal string Firmware => $"{_collection.ReleaseNumber:X4}|{_collection.PhysicalLocation}";

    public void Dispose()
    {
        _handle.Dispose();
    }

    internal static ZotacCommandTransport? Open()
    {
        var matches = HidDevices.Enumerate(0x1EE9, [0x1590]).Where(collection =>
            collection.InputLength == 65 && collection.OutputLength == 65
                                         && !string.IsNullOrWhiteSpace(collection.PhysicalLocation)).ToArray();
        return matches.Length == 1 ? new ZotacCommandTransport(matches[0]) : null;
    }

    internal void Write(byte[] report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        HidDevices.WriteOutput(_handle, _collection, report);
    }
}
