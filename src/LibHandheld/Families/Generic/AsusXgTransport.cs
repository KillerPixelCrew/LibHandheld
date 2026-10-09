using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LibHandheld.Families.RogAlly;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Generic;

/// <summary>The dock's two ACPI IDs and 300-byte HID collection; no driver tuning belongs here.</summary>
internal sealed partial class AsusXgTransport : IDisposable
{
    private const uint ConnectedId = 0x00090018;
    private const uint EnableId = 0x00090019;
    private readonly SafeFileHandle _acpi;
    private readonly HidCollection[] _lights;

    private AsusXgTransport(SafeFileHandle acpi, HidCollection[] lights)
    {
        _acpi = acpi;
        _lights = lights;
    }

    internal bool Connected => Read(ConnectedId) == 1;

    public void Dispose()
    {
        _acpi.Dispose();
    }

    internal static AsusXgTransport? Open()
    {
        var handle = CreateFile(@"\\.\ATKACPI", 0xC0000000, 3, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var lights = HidDevices.Enumerate(0x0B05, [0x1970, 0x1A9A, 0x1BC1]).Where(collection =>
            collection.FeatureLength == 300
            && (collection.ProductId != AsusAuraProtocol.Xg2025 ||
                AsusAuraProtocol.Application(collection.UsagePage, collection.Usage))
            && !string.IsNullOrEmpty(collection.PhysicalLocation)).ToArray();
        // Multiple collections of one dock are expected; multiple independent docks are ambiguous.
        if (lights.Select(collection => collection.PhysicalLocation).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1)
        {
            lights = [];
        }

        return new AsusXgTransport(handle, lights);
    }

    internal bool? ReadEnabled()
    {
        return Read(EnableId) is { } value ? value == 1 : null;
    }

    private int? Read(uint id)
    {
        try
        {
            var raw = Exchange(AsusAcpiProtocol.DeviceStatus, id, 0);
            return AsusAcpiProtocol.TryDecodeScalar(raw, out var value) ? value : null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            return null;
        }
    }

    internal void Enable(bool enabled)
    {
        if (Exchange(AsusAcpiProtocol.DeviceSet, EnableId, enabled ? 1U : 0) != 0)
        {
            throw new IOException("ASUS firmware rejected the XG Mobile change.");
        }
    }

    internal void Light(bool enabled)
    {
        Write(Encoding.ASCII.GetBytes("^ASUS Tech.Inc."));
        foreach (var collection in _lights.Where(collection => collection.ProductId == AsusAuraProtocol.Xg2025))
        {
            using var handle = HidDevices.Open(collection, false);
            HidDevices.SetFeature(handle, collection, AsusAuraProtocol.Packet(AsusAuraProtocol.Xg2025, 0xE4, 2));
            HidDevices.SetFeature(handle, collection, AsusAuraProtocol.Packet(AsusAuraProtocol.Xg2025, 0xBD, 0, 1));
        }

        Write([0x5E, 0xC5, enabled ? (byte)0x50 : (byte)0]);
    }

    internal void Fan(byte[] curve)
    {
        if (!AsusAcpiProtocol.IsValidCurve(curve))
        {
            throw new ArgumentException("Invalid XG Mobile fan curve.", nameof(curve));
        }

        var payload = new byte[19];
        payload[0] = 0x5E;
        payload[1] = 0xD1;
        payload[2] = 1;
        curve.CopyTo(payload, 3);
        Write(Encoding.ASCII.GetBytes("^ASUS Tech.Inc."));
        Write(payload);
    }

    private void Write(byte[] payload)
    {
        if (_lights.Length == 0)
        {
            throw new IOException("The XG Mobile 300-byte HID collection is absent or ambiguous.");
        }

        foreach (var collection in _lights)
        {
            using var handle = HidDevices.Open(collection, false);
            HidDevices.SetFeature(handle, collection, payload);
        }
    }

    private uint Exchange(uint method, uint id, uint value)
    {
        if (id is not (ConnectedId or EnableId) || (method == AsusAcpiProtocol.DeviceSet && id != EnableId))
        {
            throw new InvalidOperationException("Unreviewed XG Mobile ACPI operation.");
        }

        var request = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(request, method);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), id);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), value);
        var output = new byte[16];
        if (!DeviceIoControl(_acpi, AsusAcpiProtocol.Ioctl, request, 16, output, 16, out var returned, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "The XG Mobile ACPI exchange failed; write effect is unknown.");
        }

        if (returned < 4)
        {
            throw new IOException("The XG Mobile ACPI response is incomplete.");
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(output);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, [In] byte[] input, uint inputLength,
        [Out] byte[] output, uint outputLength, out uint returned, nint overlapped);
}
