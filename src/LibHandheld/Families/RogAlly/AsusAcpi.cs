// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Plugin;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.RogAlly;

/// <summary>The ATKACPI device IDs this plugin reads or writes. Nothing outside this list is sent.</summary>
/// <remarks>
///     Values are HC 1.3.1.6 <c>HandheldCompanion.Devices.ASUS/AsusACPI.cs:11-51</c>. HHD reaches the
///     same controls through Linux asus-wmi attributes (<c>adjustor/drivers/asus/__init__.py:17-19</c>),
///     which dispatch to these IDs in the kernel.
/// </remarks>
internal enum AsusAcpiId : uint
{
    /// <summary>Throttle thermal policy: 0 performance, 1 turbo, 2 silent.</summary>
    PerformanceMode = 0x00120075,

    /// <summary>SPL, the sustained package power limit in watts (HC <c>PPT_APUA3</c>).</summary>
    SustainedPower = 0x001200A3,

    /// <summary>SPPT, the slow package power limit (HC <c>PPT_APUA0</c>).</summary>
    SlowPower = 0x001200A0,

    /// <summary>FPPT, the fast package power limit (HC <c>PPT_APUC1</c>).</summary>
    FastPower = 0x001200C1,

    /// <summary>Battery charge ceiling in percent.</summary>
    ChargeLimit = 0x00120057,

    /// <summary>CPU fan reading. HC reads it as a duty cycle (<c>AsusACPI.GetFanDuty</c>).</summary>
    CpuFanSpeed = 0x00110013,

    /// <summary>GPU fan reading.</summary>
    GpuFanSpeed = 0x00110014,

    /// <summary>CPU fan curve: eight temperatures, then eight duties.</summary>
    CpuFanCurve = 0x00110024,

    /// <summary>GPU fan curve.</summary>
    GpuFanCurve = 0x00110025,

    /// <summary>Mid fan curve, which HC writes on every Ally (<c>ROGAlly.cs:317-321</c>).</summary>
    MidFanCurve = 0x00110032
}

/// <summary>Encodes and decodes ATKACPI DSTS/DEVS exchanges; nothing here touches a handle.</summary>
internal static class AsusAcpiProtocol
{
    /// <summary><c>CTL_CODE(0x22, 0x903, METHOD_BUFFERED, FILE_ANY_ACCESS)</c>, HC's <c>CONTROL_CODE</c>.</summary>
    public const uint Ioctl = 0x0022240C;

    /// <summary>"DSTS", the status query method.</summary>
    public const uint DeviceStatus = 0x53545344;

    /// <summary>"DEVS", the set method.</summary>
    public const uint DeviceSet = 0x53564544;

    public const int ResponseLength = 16;
    public const int CurveLength = 16;

    /// <summary>The DSTS status that means the firmware does not implement a device ID.</summary>
    public const uint Unsupported = 0xFFFFFFFE;

    public static bool IsReadable(AsusAcpiId id)
    {
        return Enum.IsDefined(id);
    }

    public static bool IsWritable(AsusAcpiId id)
    {
        return id is not (AsusAcpiId.CpuFanSpeed or AsusAcpiId.GpuFanSpeed) && Enum.IsDefined(id);
    }

    public static bool IsCurve(AsusAcpiId id)
    {
        return id is AsusAcpiId.CpuFanCurve or AsusAcpiId.GpuFanCurve or AsusAcpiId.MidFanCurve;
    }

    /// <summary>HC's <c>CallMethod</c> layout: method, argument length, arguments.</summary>
    /// <param name="method">Native ACPI method code, normally DSTS or DEVS.</param>
    /// <param name="id">Device ID written before the method arguments.</param>
    /// <param name="arguments">Payload bytes appended after the device ID; copied into the request.</param>
    /// <returns>A new little-endian request buffer; this encoder does not enforce the transport allowlist or call firmware.</returns>
    public static byte[] Encode(uint method, AsusAcpiId id, ReadOnlySpan<byte> arguments)
    {
        var request = new byte[12 + arguments.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(request, method);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), (uint)(4 + arguments.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), (uint)id);
        arguments.CopyTo(request.AsSpan(12));
        return request;
    }

    public static byte[] EncodeStatus(AsusAcpiId id, uint selector)
    {
        Span<byte> argument = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(argument, selector);
        return Encode(DeviceStatus, id, argument);
    }

    public static byte[] EncodeSet(AsusAcpiId id, uint value)
    {
        Span<byte> argument = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(argument, value);
        return Encode(DeviceSet, id, argument);
    }

    /// <summary>Decodes a DSTS scalar the way the reviewed scaffold reader did.</summary>
    /// <remarks>
    ///     Bit 16 is the presence flag HC subtracts (<c>AsusACPI.DeviceGet</c> returns raw - 0x10000);
    ///     the value is the low word. Anything with high bits outside 0x70000 is not a scalar.
    /// </remarks>
    /// <param name="raw">Raw DSTS return value.</param>
    /// <param name="value">Low 16-bit scalar on success, zero when absent or structurally unsupported.</param>
    /// <returns>True only when the presence flag is set and high bits match the admitted scalar form.</returns>
    public static bool TryDecodeScalar(uint raw, out int value)
    {
        value = 0;
        if (raw == Unsupported || (raw & 0x10000) == 0 || (raw & 0xFFF80000) != 0)
        {
            return false;
        }

        value = (int)(raw & 0xFFFF);
        return true;
    }

    /// <summary>HC's fan-curve selector: performance mode 1 reads profile 2 and 2 reads profile 1.</summary>
    /// <param name="performanceMode">Native mode: 1 turbo, 2 silent, otherwise default/performance.</param>
    /// <returns>Curve selector 2 for turbo, 1 for silent, or 0 for every other value.</returns>
    public static uint CurveSelector(int performanceMode)
    {
        return performanceMode switch
        {
            1 => 2,
            2 => 1,
            _ => 0
        };
    }

    /// <summary>Whether sixteen bytes are a curve this plugin may write or restore.</summary>
    /// <remarks>
    ///     Eight nondecreasing temperatures from 20 to 110 °C and eight duties no higher than 100, at
    ///     least one of them non-zero: the envelope the Ally X Lab reviewed before any curve write.
    /// </remarks>
    /// <param name="curve">Eight temperature bytes followed by eight duty bytes.</param>
    /// <returns>
    ///     True for the 16-byte admitted temperature/duty envelope with at least one nonzero duty; this does not query
    ///     hardware.
    /// </returns>
    public static bool IsValidCurve(ReadOnlySpan<byte> curve)
    {
        if (curve.Length != CurveLength)
        {
            return false;
        }

        for (var index = 0; index < 8; index++)
        {
            if (curve[index] is < 20 or > 110 || curve[8 + index] > 100
                                              || (index > 0 && curve[index] < curve[index - 1]))
            {
                return false;
            }
        }

        return curve[8..].IndexOfAnyExcept((byte)0) >= 0;
    }
}

/// <summary>The serialized ATKACPI transport.</summary>
internal interface IAsusAcpi : IDisposable
{
    /// <summary>Opens the reviewed ATKACPI handle, retaining it until disposal.</summary>
    /// <returns>True when a handle is already open or was acquired; false when Windows refuses the open.</returns>
    bool TryOpen();

    /// <summary>Queries one reviewed scalar device ID through DSTS.</summary>
    /// <param name="id">Defined device ID; arbitrary numeric IDs are refused.</param>
    /// <param name="selector">Firmware selector, normally zero.</param>
    /// <returns>Raw status word; a failed query returns zero, which is not a supported scalar value.</returns>
    uint ReadStatus(AsusAcpiId id, uint selector = 0);

    /// <summary>Queries one reviewed fan-curve buffer through DSTS.</summary>
    /// <param name="id">CPU, GPU or mid fan-curve ID.</param>
    /// <param name="selector">Profile selector produced by <see cref="AsusAcpiProtocol.CurveSelector" />.</param>
    /// <returns>Sixteen response bytes owned by the caller; failed queries return zeros and must remain unknown.</returns>
    byte[] ReadBuffer(AsusAcpiId id, uint selector);

    /// <summary>Writes one reviewed scalar through DEVS without confirming readback.</summary>
    /// <param name="id">Writable scalar ID; telemetry and curve IDs are refused.</param>
    /// <param name="value">Firmware-encoded scalar already checked against model limits.</param>
    /// <returns>The firmware response word, not proof of independently observed state.</returns>
    /// <remarks>A transport failure after dispatch is uncertain and must not cause an automatic retry.</remarks>
    uint Write(AsusAcpiId id, uint value);

    /// <summary>Writes one reviewed sixteen-byte fan curve through DEVS.</summary>
    /// <param name="id">CPU, GPU or mid fan-curve ID.</param>
    /// <param name="data">
    ///     Eight nondecreasing temperatures from 20 through 110 Celsius, then eight duties from 0 through 100
    ///     with at least one nonzero.
    /// </param>
    /// <returns>The firmware response word, not confirming readback.</returns>
    /// <remarks>
    ///     The transport rejects invalid curve shapes before dispatch; model-specific limits remain the caller's
    ///     responsibility.
    /// </remarks>
    uint WriteBuffer(AsusAcpiId id, ReadOnlySpan<byte> data);
}

/// <summary>Reads and writes through the ASUS System Control Interface driver.</summary>
/// <remarks>
///     Synchronous by nature: a driver call cannot be cancelled, so callers are the plugin's own
///     serialized workers, never the UI thread. INIT, WDOG and arbitrary IDs are not exposed.
/// </remarks>
internal sealed partial class WindowsAsusAcpi : IAsusAcpi
{
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;

    private readonly Lock _gate = new();
    private bool _disposed;
    private SafeFileHandle? _handle;

    /// <inheritdoc />
    public bool TryOpen()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is { IsInvalid: false })
            {
                return true;
            }

            // HC opens the same path with read/write access (AsusACPI.cs:118).
            var handle = CreateFile(@"\\.\ATKACPI", GenericReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return false;
            }

            _handle = handle;
            return true;
        }
    }

    /// <inheritdoc />
    public uint ReadStatus(AsusAcpiId id, uint selector = 0)
    {
        if (!AsusAcpiProtocol.IsReadable(id))
        {
            throw new InvalidOperationException($"ATKACPI device 0x{(uint)id:X8} is not on the reviewed list.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(Read(AsusAcpiProtocol.EncodeStatus(id, selector), 4));
    }

    /// <inheritdoc />
    public byte[] ReadBuffer(AsusAcpiId id, uint selector)
    {
        if (!AsusAcpiProtocol.IsCurve(id))
        {
            throw new InvalidOperationException($"ATKACPI device 0x{(uint)id:X8} is not a reviewed buffer.");
        }

        return Read(AsusAcpiProtocol.EncodeStatus(id, selector), AsusAcpiProtocol.CurveLength);
    }

    /// <inheritdoc />
    public uint Write(AsusAcpiId id, uint value)
    {
        if (!AsusAcpiProtocol.IsWritable(id) || AsusAcpiProtocol.IsCurve(id))
        {
            throw new InvalidOperationException($"ATKACPI device 0x{(uint)id:X8} is not a reviewed scalar write.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(Exchange(AsusAcpiProtocol.EncodeSet(id, value), 4));
    }

    /// <inheritdoc />
    public uint WriteBuffer(AsusAcpiId id, ReadOnlySpan<byte> data)
    {
        if (!AsusAcpiProtocol.IsCurve(id) || !AsusAcpiProtocol.IsValidCurve(data))
        {
            throw new InvalidOperationException($"ATKACPI device 0x{(uint)id:X8} was sent an unreviewed buffer.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(
            Exchange(AsusAcpiProtocol.Encode(AsusAcpiProtocol.DeviceSet, id, data), 4));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _handle?.Dispose();
            _handle = null;
        }
    }

    /// <summary>A DSTS query. A failed call reads as all zeros, which decodes as unsupported.</summary>
    /// <remarks>
    ///     HC ignores the <c>DeviceIoControl</c> result and reads its zeroed buffer (<c>AsusACPI.Control</c>).
    ///     A query changes nothing, so a firmware that refuses one (the Xbox Ally X refuses the fan-curve
    ///     reads) must leave the value unknown rather than fault the service that asked.
    /// </remarks>
    private byte[] Read(byte[] request, int minimumResponse)
    {
        try
        {
            return Exchange(request, minimumResponse);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            DeviceTrace.Change("acpi-read", $"0x{BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)):X8}",
                $"ATKACPI query refused ({ex.Message}); treated as unsupported.", DeviceTraceLevel.Warn);
            return new byte[Math.Max(minimumResponse, AsusAcpiProtocol.ResponseLength)];
        }
    }

    private byte[] Exchange(byte[] request, int minimumResponse)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_handle is not { IsInvalid: false } handle)
            {
                throw new IOException("The ASUS System Control Interface is not open.");
            }

            var response = new byte[AsusAcpiProtocol.ResponseLength];
            if (!DeviceIoControl(handle, AsusAcpiProtocol.Ioctl, request, (uint)request.Length, response,
                    (uint)response.Length, out var returned, IntPtr.Zero))
            {
                // A failed write does not prove the firmware ignored it; callers never retry.
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ATKACPI exchange failed.");
            }

            if (returned < minimumResponse || returned > response.Length)
            {
                throw new IOException($"ATKACPI returned {returned} bytes where {minimumResponse} were expected.");
            }

            return returned == response.Length ? response : [.. response.Take((int)returned)];
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle file,
        uint ioctl,
        [In] byte[] input,
        uint inputLength,
        [Out] byte[] output,
        uint outputLength,
        out uint returned,
        IntPtr overlapped);
}
