using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Gpd;

internal sealed record GpdControlsSnapshot(string Firmware, byte[] Header, byte[] Configuration, bool Version2);

/// <summary>WinControls' two wire formats, reimplemented from documented packet/offset facts.</summary>
internal sealed class GpdControlsProtocol : IDisposable
{
    private static readonly Lock Bus = new();
    private readonly HidCollection _collection;
    private readonly SafeFileHandle _handle;
    private readonly bool _version2;

    private GpdControlsProtocol(HidCollection collection)
    {
        _collection = collection;
        _handle = HidDevices.Open(collection, false);
        _version2 = collection.ProductId is 0x0137 or 0x002D;
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    internal static GpdControlsProtocol? Open()
    {
        var candidates = HidDevices.Enumerate(0x2F24, [0x0135, 0x0137]).Concat(HidDevices.Enumerate(0x045E, [0x002D]))
            .Where(collection =>
                collection.UsagePage == 0xFF00 && collection.Usage == 1 &&
                collection.FeatureLength is >= 33 and <= 1024).ToArray();
        return candidates.Length == 1 ? new GpdControlsProtocol(candidates[0]) : null;
    }

    internal GpdControlsSnapshot Read(CancellationToken token)
    {
        lock (Bus)
        {
            if (_version2)
            {
                ReadyV2(0x21, token);
                ReadyV2(0x2B, token);
                Send(V2Packet(0x44, 2, 0, [0, 4]), token);
                var bytes = new List<byte>(1036);
                for (var page = 0; page < 20; page++)
                {
                    var response = Receive(token);
                    if (response[1] != 0x44 || response[2] > 56)
                    {
                        throw new InvalidDataException("GPD V2 config packet shape changed.");
                    }

                    bytes.AddRange(response.AsSpan(8, response[2]).ToArray());
                    if (response[2] < 56)
                    {
                        break;
                    }
                }

                if (bytes.Count != 1024)
                {
                    throw new InvalidDataException("The GPD V2 config transfer is incomplete.");
                }

                var data = bytes.ToArray();
                Send(V2Packet(0x41), token);
                var version = FirmwareV2(Receive(token));
                return new GpdControlsSnapshot(version, data[..12], data[12..], true);
            }

            Send(V1Packet(0x10), token);
            var firmware = FirmwareV1(Receive(token));
            var config = new byte[128];
            var withIdRemoved = new byte[128];
            for (byte page = 0; page < 4; page++)
            {
                Send(V1Packet(0x11, page), token);
                var response = Receive(token);
                // hidraw and Windows report-ID framing can differ. Accept only the 128-byte
                // configuration whose checksum the firmware actually returns, never guess an offset.
                response.AsSpan(0, 32).CopyTo(config.AsSpan(page * 32));
                response.AsSpan(1, 32).CopyTo(withIdRemoved.AsSpan(page * 32));
            }

            Send(V1Packet(0x12), token);
            var final = Receive(token);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(final.AsSpan(24, 4));
            if (FirmwareV1(final) != firmware)
            {
                throw new InvalidDataException("The GPD configuration checksum or firmware changed during reading.");
            }

            if (checksum == Sum(config) && checksum == Sum(withIdRemoved) &&
                !config.AsSpan().SequenceEqual(withIdRemoved))
            {
                throw new InvalidDataException("The GPD report-ID framing is ambiguous.");
            }

            if (checksum != Sum(config))
            {
                if (checksum != Sum(withIdRemoved))
                {
                    throw new InvalidDataException(
                        "The GPD configuration checksum did not match either report framing.");
                }

                config = withIdRemoved;
            }

            return new GpdControlsSnapshot(firmware, [], config, false);
        }
    }

    internal void Write(GpdControlsSnapshot original, ReadOnlySpan<byte> configuration, CancellationToken token)
    {
        lock (Bus)
        {
            if (original.Version2 != _version2 || configuration.Length != original.Configuration.Length)
            {
                throw new InvalidDataException("The GPD controller configuration belongs to a different wire format.");
            }

            if (_version2)
            {
                Send(V2Packet(0x41), token);
                if (FirmwareV2(Receive(token)) != original.Firmware)
                {
                    throw new InvalidDataException("GPD controller firmware changed.");
                }

                ReadyV2(0x21, token);
                var stream = new byte[1024];
                original.Header.CopyTo(stream, 0);
                configuration.CopyTo(stream.AsSpan(12));
                for (ushort offset = 0; offset < stream.Length; offset += 56)
                {
                    var size = (byte)Math.Min(56, stream.Length - offset);
                    Send(V2Packet(0x43, size, offset, stream.AsSpan(offset, size)), token, false);
                }

                Send(V2Packet(0x27, 2, 0, [0, 4]), token);
                var prepare = Receive(token);
                if (prepare[8] == 0xE2 || BinaryPrimitives.ReadUInt16LittleEndian(prepare.AsSpan(10, 2)) !=
                    (Sum(stream) & 0xFFFF))
                {
                    throw new InvalidDataException(
                        "GPD refused the configuration buffer or its checksum; EEPROM commit was not dispatched.");
                }

                Send(V2Packet(0x22), token);
                Reject(Receive(token));
                ReadyV2(0x21, token);
                Send(V2Packet(0x25, 4, 0, [0, 4]), token);
                Reject(Receive(token));
                Send(V2Packet(0x22), token);
                return;
            }

            Send(V1Packet(0x21), token);
            if (FirmwareV1(Receive(token)) != original.Firmware)
            {
                throw new InvalidDataException("GPD controller firmware changed.");
            }

            for (byte page = 0; page < 8; page++)
            {
                Send(V1Packet(0x21, page, configuration.Slice(page * 16, 16)), token, false);
            }

            Send(V1Packet(0x22), token);
            var accepted = Receive(token);
            if (FirmwareV1(accepted) != original.Firmware ||
                BinaryPrimitives.ReadUInt32LittleEndian(accepted.AsSpan(24, 4)) != Sum(configuration))
            {
                throw new InvalidDataException(
                    "GPD controller config checksum did not match; EEPROM commit was not dispatched.");
            }

            Send(V1Packet(0x23), token);
        }
    }

    internal static byte[] V1Packet(byte command, byte page = 0, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var packet = new byte[33];
        packet[0] = 1;
        packet[1] = 0xA5;
        packet[2] = command;
        packet[3] = 0x5A;
        packet[4] = (byte)(0xFF ^ command);
        packet[6] = page;
        payload.CopyTo(packet.AsSpan(8));
        return packet;
    }

    internal static byte[] V2Packet(byte command, byte size = 0, ushort offset = 0,
        ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > 56)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var packet = new byte[64];
        packet[0] = 1;
        packet[1] = command;
        packet[2] = size;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4, 2), offset);
        payload.CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6, 2), (ushort)(Sum(packet.AsSpan(8)) & 0xFFFF));
        return packet;
    }

    private static uint Sum(ReadOnlySpan<byte> bytes)
    {
        uint total = 0;
        foreach (var value in bytes)
        {
            total += value;
        }

        return total;
    }

    private static string FirmwareV1(ReadOnlySpan<byte> response)
    {
        if (response.Length < 32 || response[8] != 0xAA)
        {
            throw new InvalidDataException("GPD controller is not ready.");
        }

        var gamepadLimit = response[9] switch { 3 => 0x15, 4 => 9, 5 => 0x10, _ => -1 };
        var extendLimit = response[11] switch { 1 => 0x23, 4 => 7, 5 => 4, _ => -1 };
        if (response[10] > gamepadLimit || response[12] > extendLimit)
        {
            throw new NotSupportedException("The GPD configuration firmware is outside the known layout versions.");
        }

        return $"X{response[9]}{response[10]:X2}K{response[11]}{response[12]:X2}";
    }

    private static string FirmwareV2(ReadOnlySpan<byte> response)
    {
        if (response.Length < 14 || response[1] != 0x41 || response[11] != 6 || response[10] > 3 || response[13] != 8 ||
            response[12] > 1)
        {
            throw new NotSupportedException("The GPD V2 firmware is outside the known configuration layout.");
        }

        return $"X{response[11]}{response[10]:X2}K{response[13]}{response[12]:X2}";
    }

    private void ReadyV2(byte command, CancellationToken token)
    {
        Send(V2Packet(command), token);
        var response = Receive(token);
        if (response[8] != 0xAA)
        {
            throw new IOException("GPD config handshake was refused.");
        }
    }

    private static void Reject(ReadOnlySpan<byte> response)
    {
        if (response[8] == 0xE2)
        {
            throw new IOException("GPD configuration commit was refused; its effect is uncertain.");
        }
    }

    private void Send(byte[] packet, CancellationToken token, bool pause = true)
    {
        token.ThrowIfCancellationRequested();
        HidDevices.SetFeature(_handle, _collection, packet);
        if (pause && token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50)))
        {
            token.ThrowIfCancellationRequested();
        }
    }

    private byte[] Receive(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var report = new byte[_version2 ? 64 : _collection.FeatureLength];
        report[0] = 1;
        var success = _version2
            ? HidDevices.GetInputReport(_handle, report)
            : HidDevices.GetFeature(_handle, report);
        if (!success)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return report;
    }
}
