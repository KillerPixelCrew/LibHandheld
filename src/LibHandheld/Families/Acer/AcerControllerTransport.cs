using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>The controller's volatile lighting profile lane. Firmware mappings and ROM are never written.</summary>
internal sealed class AcerControllerTransport : IAsyncDisposable
{
    internal const int LightingOffset = 586;
    internal const int LightingLength = 883;
    private readonly FileStream _stream;

    private AcerControllerTransport(HidCollection collection)
    {
        _stream = HidDevices.OpenStream(collection);
        Release = collection.ReleaseNumber;
    }

    internal ushort Release { get; }

    internal static AcerControllerTransport? Open()
    {
        var collection = HidDevices.Unique(HidDevices.Enumerate(0x0502, [0xF901]),
            item => item.UsagePage == 0xFFA0 && item.Usage == 1
                    && item.InputLength == 64 && item.OutputLength == 64 && item.ReleaseNumber == 0x0100);
        return collection is null ? null : new AcerControllerTransport(collection);
    }

    internal async ValueTask<byte[]> ReadLightingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // This dedicated command handle also receives other clients' query replies. Discard
        // already queued input before requesting a fresh snapshot; never flush an OEM reader.
        if (!NativeHid.HidD_FlushQueue(_stream.SafeFileHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Atlas controller input queue could not be flushed.");
        }

        var lighting = new byte[LightingLength];
        for (var offset = 0; offset < lighting.Length; offset += 55)
        {
            var count = Math.Min(55, lighting.Length - offset);
            var address = LightingOffset + offset;
            var request = Packet(command: 4, address, count);
            var candidateHeader = new byte[9];
            byte[] response;
            try
            {
                response = await HidDevices.ExchangeAsync(_stream, request, responseLength: 64,
                    reply =>
                    {
                        if (reply[4] == 5 || candidateHeader[0] == 0)
                        {
                            reply.AsSpan(0, 9).CopyTo(candidateHeader);
                        }

                        return reply[0] == 0x10 && reply[3] == 0x3C && reply[4] == 5 && reply[5] == 1
                               && BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)) == address
                               && reply[8] == count;
                    }, timeout: TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException($"No correlated Atlas lighting reply at {address}; header {Convert.ToHexString(candidateHeader)}.");
            }
            response.AsSpan(9, count).CopyTo(lighting.AsSpan(offset));
        }

        return lighting;
    }

    internal async ValueTask WriteLightingAsync(byte[] lighting, CancellationToken token, Action? dispatched = null,
        byte[]? previous = null)
    {
        if (lighting.Length != LightingLength)
        {
            throw new ArgumentException("A complete retained Atlas lighting section is required.", nameof(lighting));
        }

        var first = 0;
        var end = lighting.Length;
        if (previous is { Length: LightingLength })
        {
            while (first < end && previous[first] == lighting[first])
            {
                first++;
            }

            while (end > first && previous[end - 1] == lighting[end - 1])
            {
                end--;
            }
        }

        for (var offset = first; offset < end; offset += 55)
        {
            var count = Math.Min(55, end - offset);
            var request = Packet(command: 0x21, LightingOffset + offset, count);
            lighting.AsSpan(offset, count).CopyTo(request.AsSpan(9));
            token.ThrowIfCancellationRequested();
            dispatched?.Invoke();
            await _stream.WriteAsync(request, token).ConfigureAwait(false);
            await _stream.FlushAsync(token).ConfigureAwait(false);
        }
    }

    internal static byte[] Packet(byte command, int offset, int count)
    {
        if (command is not (4 or 0x21) || count is < 1 or > 55
            || offset < LightingOffset || offset + count > LightingOffset + LightingLength)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Only the retained lighting section may be addressed.");
        }

        var packet = new byte[64];
        packet[0] = 0x0F;
        packet[3] = 0x3C;
        packet[4] = command;
        packet[5] = command == 0x21 ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), checked((ushort)offset));
        packet[8] = checked((byte)count);
        return packet;
    }

    public ValueTask DisposeAsync()
    {
        return _stream.DisposeAsync();
    }
}
