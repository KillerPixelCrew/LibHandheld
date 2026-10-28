using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>The Atlas controller's dedicated command lane for reversible mode and volatile lighting changes.</summary>
/// <remarks>
///     Measured USB 0502:F901 release 0100, FFA0:0001, uses 64-byte input/output reports. The outer
///     header is 0F 00 00 3C for requests and 10 00 00 3C for replies. Profile offsets are big endian,
///     unlike the separate EC transport. Only lighting RAM and immediate gamepad/rear-key modes are
///     writable here: key mappings, sensor configuration, calibration and command 22 ROM save are excluded.
///     The owning service serializes this handle's operations; flushing it never flushes an OEM reader.
/// </remarks>
internal sealed class AcerControllerTransport : IAsyncDisposable, IAcerControllerModeTransport
{
    /// <summary>Byte offset of the lighting section in the complete vendor profile.</summary>
    internal const int LightingOffset = 586;
    /// <summary>Complete retained section: selected-animation byte, four 220-byte animations and trailing bytes.</summary>
    internal const int LightingLength = 883;
    private FileStream _stream;
    private HidCollection _collection;

    private AcerControllerTransport(HidCollection collection)
    {
        _collection = collection;
        _stream = HidDevices.OpenStream(collection);
        Release = collection.ReleaseNumber;
    }

    internal ushort Release { get; }

    private void FlushReplies()
    {
        if (!NativeHid.HidD_FlushQueue(_stream.SafeFileHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Atlas controller input queue could not be flushed.");
        }
    }

    /// <summary>Discards stale command replies, sends query 26 and reads the gamepad/rear-key bytes from reply 27.</summary>
    /// <remarks>Reply bytes 5 and 6 are the two owned modes; other returned mode/status bytes are not rewritten.</remarks>
    public async ValueTask<AcerControllerMode> ReadModeAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        FlushReplies();
        var reply = await HidDevices.ExchangeAsync(_stream, ModePacket(), responseLength: 64,
            data => data[0] == 0x10 && data[3] == 0x3C && data[4] == 0x27,
            timeout: TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
        return reply[5] <= 6 && reply[6] <= 2
            ? new AcerControllerMode(reply[5], reply[6])
            : throw new IOException("Unsupported Atlas controller mode reply.");
    }

    /// <summary>Writes immediate command 24 with gamepad and rear-key modes, followed by the vendor's 20 ms pacing.</summary>
    /// <remarks>
    ///     There is no acknowledgement/retry in this method; recovery reads the mode separately.
    ///     Cancellation can reject before dispatch but must not be interpreted as proof that a submitted
    ///     mode change did not happen.
    /// </remarks>
    public async ValueTask WriteModeAsync(AcerControllerMode mode, CancellationToken token)
    {
        var packet = ModePacket(mode);
        token.ThrowIfCancellationRequested();
        await _stream.WriteAsync(packet, token).ConfigureAwait(false);
        await _stream.FlushAsync(token).ConfigureAwait(false);
        await Task.Delay(millisecondsDelay: 20, CancellationToken.None).ConfigureAwait(false);
        var product = mode.Gamepad == 2 ? (ushort)0xF90A : (ushort)0xF901;
        if (_collection.ProductId != product)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            // The acknowledged dispatch changes the USB topology. Poll only for arrival; never
            // resend the mode command when Windows has not finished enumerating its replacement.
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                var replacement = HidDevices.Unique(HidDevices.Enumerate(0x0502, [product]),
                    item => IsCommandCollection(item)
                            && HidDevices.SamePhysicalLocation(item.PhysicalLocation, _collection.PhysicalLocation));
                if (replacement is not null)
                {
                    _stream = HidDevices.OpenStream(replacement);
                    _collection = replacement;
                    return;
                }
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
            throw new IOException("The Atlas mode command was sent, but its replacement command interface did not arrive.");
        }
    }

    /// <summary>Builds query 26 when null, or mode command 24 with payload bytes at offsets 5/6.</summary>
    internal static byte[] ModePacket(AcerControllerMode? mode = null)
    {
        if (mode is { } value && (value.Gamepad > 6 || value.RearKeys > 2))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        var packet = new byte[64];
        packet[0] = 0x0F;
        packet[3] = 0x3C;
        packet[4] = mode is null ? (byte)0x26 : (byte)0x24;
        if (mode is { } selected)
        {
            packet[5] = selected.Gamepad;
            packet[6] = selected.RearKeys;
        }

        return packet;
    }

    /// <summary>Opens the measured command lane in either supported controller topology.</summary>
    internal static AcerControllerTransport? Open()
    {
        var collection = HidDevices.Unique(HidDevices.Enumerate(0x0502, [0xF901, 0xF90A]), IsCommandCollection);
        return collection is null ? null : new AcerControllerTransport(collection);
    }

    private static bool IsCommandCollection(HidCollection item) =>
        item.InputLength == 64 && item.OutputLength == 64 && item.ReleaseNumber == 0x0100
        && (item.ProductId == 0xF901 && item.UsagePage == 0xFFA0 && item.Usage == 1
            || item.ProductId == 0xF90A && item.UsagePage == 0xFFF0 && item.Usage == 0x40);

    /// <summary>Reads all 883 retained lighting bytes in correlated chunks of at most 55 data bytes.</summary>
    /// <remarks>
    ///     Request 04 selects profile 0, while reply 05 identifies returned RAM profile 1. Offset and
    ///     count must match the outstanding request. Unrelated command replies are ignored until the
    ///     one-second chunk deadline; partial sections are never returned as usable originals.
    /// </remarks>
    internal async ValueTask<byte[]> ReadLightingAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // This dedicated command handle also receives other clients' query replies. Discard
        // already queued input before requesting a fresh snapshot; never flush an OEM reader.
        FlushReplies();
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

    /// <summary>Writes command 21/profile 1 RAM chunks within the retained lighting section only.</summary>
    /// <param name="lighting">The complete edited or original section, preserving every unrelated byte.</param>
    /// <param name="token">Checked before each chunk; a cancellation after a prior chunk can leave a partial edit.</param>
    /// <param name="dispatched">Marks each submitted chunk so the service can discard uncertain cached observations.</param>
    /// <param name="previous">Known previous bytes for trimming the changed range; null forces full-section recovery.</param>
    /// <remarks>No per-chunk acknowledgement or automatic retry is performed, and command 22 ROM save is never sent.</remarks>
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

    /// <summary>Builds the nine-byte profile header and refuses addresses outside the lighting section.</summary>
    /// <remarks>Bytes 4/5 are command/profile, 6/7 the big-endian absolute offset, 8 the payload count; data starts at 9.</remarks>
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

/// <summary>The two immediate mode fields owned by the controller lifecycle.</summary>
/// <param name="Gamepad">Offline 0, XInput 1, DInput 2, Special 3, Desktop 4, BIOS 5 or Testing 6.</param>
/// <param name="RearKeys">Macro 0, Combination/native reporting 1, or Disabled 2.</param>
internal sealed record AcerControllerMode(byte Gamepad, byte RearKeys);

/// <summary>Mode-only transport seam used by the lifecycle and hardware-free protocol fixtures.</summary>
internal interface IAcerControllerModeTransport : IAsyncDisposable
{
    ValueTask<AcerControllerMode> ReadModeAsync(CancellationToken token);
    ValueTask WriteModeAsync(AcerControllerMode mode, CancellationToken token);
}
