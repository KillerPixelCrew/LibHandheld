using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Acer;

/// <summary>Predator EC feature transactions, serialized with Acer's own hardware service.</summary>
/// <remarks>
///     This is the 1025:174B I2C HID FF05:0001 feature collection, not raw EC port/register access and
///     not the USB gamepad. Windows report byte 0 is ID A0; all offsets used by services refer to the
///     payload after that byte. Payload words are little endian: request A000, command at offset 2,
///     successful reply E000 with the same command. SetFeature alone is not firmware acknowledgement.
///     Each synchronous exchange holds both the per-handle lock and Acer's named mutex across
///     SetFeature, 20 ms pacing and GetFeature, keeping the thread-affine mutex on one thread.
/// </remarks>
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

    /// <summary>Opens one unambiguous EC collection selected by HID attributes, including non-USB paths.</summary>
    /// <returns>An owned synchronous feature handle, or null if the measured endpoint is absent or ambiguous.</returns>
    internal static AcerEcTransport? Open()
    {
        // I2C HID paths use HID#1025174B, without USB VID_/PID_ components.
        var collection = HidDevices.Unique(HidDevices.EnumerateAll(),
            item => item.VendorId == 0x1025 && item.ProductId == 0x174B
                    && item.UsagePage == 0xFF05 && item.Usage == 1 && item.FeatureLength >= 29);
        return collection is null ? null : new AcerEcTransport(collection);
    }

    /// <summary>Performs one request/reply exchange without retrying a possibly completed command.</summary>
    /// <param name="payload">A000-prefixed request excluding report ID, bounded by the descriptor and 64 bytes.</param>
    /// <param name="token">Checked before mutex acquisition and immediately before dispatch.</param>
    /// <param name="dispatched">Called just before SetFeature so callers distinguish rejection from uncertain effects.</param>
    /// <returns>The full descriptor-sized reply payload, with the report ID removed and framing validated.</returns>
    /// <remarks>
    ///     After dispatch, cancellation cannot abandon the acknowledgement half. A missing or mismatched
    ///     reply throws with effect unknown; callers must not resend automatically. Timeout before owning
    ///     the mutex has no hardware effect. The mutex is released even after native or framing failures.
    /// </remarks>
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

    /// <summary>Releases native resources only; each service owns restoration before disposal.</summary>
    public void Dispose()
    {
        _handle.Dispose();
        _mutex.Dispose();
    }
}

/// <summary>The closed EC exchange used by Atlas controls and protocol fixtures.</summary>
internal interface IAcerEcTransport : IDisposable
{
    /// <summary>Exchanges one EC payload; the dispatch callback marks the uncertain-write boundary.</summary>
    byte[] Exchange(ReadOnlySpan<byte> payload, CancellationToken token, Action? dispatched = null);
}
