using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Acer;

/// <summary>Predator EC feature transactions, serialized with Acer's own hardware service.</summary>
internal sealed class AcerEcTransport : IAcerEcTransport
{
    private readonly HidCollection _collection;
    private readonly SafeFileHandle _handle;
    private readonly Mutex _mutex = new(false, "Global\\ECHIDFeatureVID_1025&PID_174B");
    private readonly Lock _gate = new();

    private AcerEcTransport(HidCollection collection)
    {
        _collection = collection;
        _handle = HidDevices.Open(collection, overlapped: false);
    }

    internal static AcerEcTransport? Open()
    {
        // I2C HID paths use HID#1025174B, without USB VID_/PID_ components.
        var collection = HidDevices.Unique(HidDevices.EnumerateAll(),
            item => item.VendorId == 0x1025 && item.ProductId == 0x174B
                    && item.UsagePage == 0xFF05 && item.Usage == 1 && item.FeatureLength >= 29);
        return collection is null ? null : new AcerEcTransport(collection);
    }

    public byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null)
    {
        if (payload.Length < 4 || payload.Length > Math.Min(64, _collection.FeatureLength - 1)
            || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 0xA000)
        {
            throw new ArgumentException("Invalid Acer EC request.", nameof(payload));
        }

        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            var owned = false;
            try
            {
                try
                {
                    owned = _mutex.WaitOne(TimeSpan.FromSeconds(1));
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (!owned)
                {
                    throw new TimeoutException("Acer EC is busy; no request was dispatched.");
                }

                token.ThrowIfCancellationRequested();
                var report = new byte[_collection.FeatureLength];
                report[0] = 0xA0;
                payload.CopyTo(report.AsSpan(1));
                dispatched?.Invoke();
                HidDevices.SetFeature(_handle, _collection, report);
                // Complete an already dispatched transaction even if its caller cancels the wait.
                Thread.Sleep(millisecondsTimeout: 20);
                Array.Clear(report);
                report[0] = 0xA0;
                if (!HidDevices.GetFeature(_handle, report))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Acer EC reply failed; effect unknown.");
                }

                if (report[0] != 0xA0 || BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(1)) != 0xE000
                    || BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(3))
                    != BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]))
                {
                    throw new IOException("Acer EC did not acknowledge the requested command; effect unknown.");
                }

                return report[1..];
            }
            finally
            {
                if (owned)
                {
                    _mutex.ReleaseMutex();
                }
            }
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
        _mutex.Dispose();
    }
}

/// <summary>The closed EC exchange used by Atlas controls and protocol fixtures.</summary>
internal interface IAcerEcTransport : IDisposable
{
    byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null);
}
