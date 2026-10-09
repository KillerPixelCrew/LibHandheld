using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.SteamDeck;

internal interface INeptuneDevice : IDisposable
{
    string FirmwareIdentity { get; }
    IReadOnlyList<PhysicalDeviceIdentity> Identities { get; }
    int Read(byte[] buffer);
    void StopReading();
    ushort? ReadSetting(byte id);
    void WriteSetting(byte id, ushort value);
    void Rumble(HapticOutputFrame frame);
}

/// <summary>One exact physical Neptune collection; feature writes and blocking reads own separate handles.</summary>
internal sealed class NeptuneDevice : INeptuneDevice
{
    private readonly SafeFileHandle _feature;
    private readonly Lock _gate = new();
    private readonly SafeFileHandle _input;
    private readonly ushort _inputLength;
    private readonly EventWaitHandle _readEvent = new(false, EventResetMode.ManualReset);
    private readonly Lock _readGate = new();
    private readonly byte[] _request;
    private bool _disposed;
    private bool _readStopped;

    private NeptuneDevice(HidCollection collection)
    {
        _feature = HidDevices.Open(collection, false);
        try
        {
            _input = HidDevices.Open(collection, true);
            _inputLength = collection.InputLength;
            _request = new byte[collection.FeatureLength];
            FirmwareIdentity = $"{collection.ReleaseNumber:X4}|{ReadSerial()}|{collection.PhysicalLocation}";
            Identities = Array.AsReadOnly(HidDevices.PresentNodes(0x28DE, [0x1205])
                .Where(node => HidDevices.SamePhysicalLocation(node.PhysicalLocation, collection.PhysicalLocation))
                .Select(node => new PhysicalDeviceIdentity
                {
                    InstancePath = node.InstancePath,
                    LocationPath = node.PhysicalLocation, VendorId = "28DE", ProductId = "1205", RequiresHiding = true
                }).ToArray());
            if (Identities.Count == 0)
            {
                throw new IOException("The Neptune controller has no correlated physical device nodes.");
            }
        }
        catch
        {
            _input?.Dispose();
            _feature.Dispose();
            _readEvent.Dispose();
            throw;
        }
    }

    public string FirmwareIdentity { get; }
    public IReadOnlyList<PhysicalDeviceIdentity> Identities { get; }

    public unsafe int Read(byte[] buffer)
    {
        var overlapped = new NativeOverlapped { EventHandle = _readEvent.SafeWaitHandle.DangerousGetHandle() };
        _readEvent.Reset();
        fixed (byte* bytes = buffer)
        {
            uint read;
            lock (_readGate)
            {
                if (_readStopped)
                {
                    throw new OperationCanceledException();
                }

                if (!ReadFile(_input, bytes, _inputLength, out read, &overlapped)
                    && Marshal.GetLastPInvokeError() != 997)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            if (!GetOverlappedResult(_input, &overlapped, out read, true))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return checked((int)read);
        }
    }

    public void StopReading()
    {
        lock (_readGate)
        {
            _readStopped = true;
            CancelIoEx(_input, IntPtr.Zero);
        }
    }

    public ushort? ReadSetting(byte id)
    {
        lock (_gate)
        {
            Prepare(0x89, 3);
            _request[3] = id;
            Send();
            Array.Clear(_request);
            if (!HidD_GetFeature(_feature, _request, _request.Length) || _request[1] != 0x89)
            {
                return null;
            }

            var length = Math.Min(_request[2], _request.Length - 3);
            for (var offset = 3; offset + 2 < length + 3; offset += 3)
            {
                if (_request[offset] == id)
                {
                    return BinaryPrimitives.ReadUInt16LittleEndian(_request.AsSpan(offset + 1));
                }
            }

            return null;
        }
    }

    public void WriteSetting(byte id, ushort value)
    {
        lock (_gate)
        {
            Prepare(0x87, 3);
            _request[3] = id;
            BinaryPrimitives.WriteUInt16LittleEndian(_request.AsSpan(4), value);
            Send();
        }
    }

    public void Rumble(HapticOutputFrame frame)
    {
        lock (_gate)
        {
            // Valve's simple rumble command directly drives both trackpad actuators and accepts explicit zero.
            ObjectDisposedException.ThrowIf(_disposed, this);
            NeptuneReport.EncodeRumble(_request, frame);
            Send();
        }
    }

    public void Dispose()
    {
        // Closing the input handle first interrupts a blocked reader before waiting for feature ownership.
        StopReading();
        _input.Dispose();
        lock (_gate)
        {
            _disposed = true;
            _feature.Dispose();
            _readEvent.Dispose();
        }
    }

    internal static bool IsCollection(HidCollection collection)
    {
        return collection.VendorId == 0x28DE
               && collection.ProductId == 0x1205 && collection.InputLength is 64 or 65
               && collection.FeatureLength >= 65 && collection.UsagePage >= 0xFF00
               && collection.DevicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(collection.PhysicalLocation);
    }

    internal static NeptuneDevice Open()
    {
        var collections = HidDevices.Enumerate(0x28DE, [0x1205]).Where(IsCollection).ToArray();
        if (collections.Length != 1)
        {
            throw new IOException("Exactly one physical Valve Neptune controller collection is required.");
        }

        return new NeptuneDevice(collections[0]);
    }

    private string ReadSerial()
    {
        lock (_gate)
        {
            Prepare(0xAE, 0x15);
            _request[3] = 1;
            Send();
            Array.Clear(_request);
            if (!HidD_GetFeature(_feature, _request, _request.Length) || _request[1] != 0xAE)
            {
                return "serial-unavailable";
            }

            return Encoding.ASCII.GetString(_request, 4, Math.Max(0, Math.Min(_request[2] - 1, _request.Length - 4)))
                .TrimEnd('\0');
        }
    }

    private void Prepare(byte command, byte length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Array.Clear(_request);
        _request[1] = command;
        _request[2] = length;
    }

    private void Send()
    {
        if (!HidD_SetFeature(_feature, _request, _request.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "Valve Neptune feature command failed; completion is unknown.");
        }
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_SetFeature(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool HidD_GetFeature(SafeFileHandle handle, byte[] buffer, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe extern bool ReadFile(SafeFileHandle handle, byte* buffer, uint bytes,
        out uint read, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe extern bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped,
        out uint read, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
