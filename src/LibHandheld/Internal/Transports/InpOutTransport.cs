using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Internal.Transports;

/// <summary>The pinned InpOut ABI; the pre-existing driver must be ready before DLL loading.</summary>
/// <remarks>
///     Each <see cref="Open" /> returns its own handle over one process-wide driver mapping per pinned
///     library, so a Steam Deck's EC memory, clock mailbox and profile power transport share a single load of
///     the DLL and one gate. Disposing a handle releases only that handle; the library is freed when the
///     last handle is disposed. Port writes and physical-memory mappings share the driver's gate. Families
///     decide which ports and addresses they reach; this transport only carries the driver calls.
/// </remarks>
internal sealed class InpOutTransport : IDisposable
{
    private static readonly Lock Drivers = new();
    private static readonly Dictionary<string, Driver> OpenDrivers = new(StringComparer.OrdinalIgnoreCase);

    private readonly Driver _driver;
    private int _disposed;

    private InpOutTransport(Driver driver)
    {
        _driver = driver;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (Drivers)
        {
            if (--_driver.Handles > 0)
            {
                return;
            }

            OpenDrivers.Remove(_driver.Path);
        }

        _driver.Dispose();
    }

    /// <summary>A handle on the pinned library at <paramref name="path" />, loading it when no handle holds it yet.</summary>
    /// <param name="path">The host-supplied pinned InpOut library.</param>
    /// <returns>A handle the caller disposes once.</returns>
    internal static InpOutTransport Open(string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.GetFullPath(path);
        lock (Drivers)
        {
            if (!OpenDrivers.TryGetValue(path, out var driver))
            {
                driver = Driver.Open(path);
                OpenDrivers.Add(path, driver);
            }

            driver.Handles++;
            return new InpOutTransport(driver);
        }
    }

    /// <summary>Acknowledged port-byte writes, in order, under one gate hold; an uncertain dispatch is never retried.</summary>
    /// <param name="requests">Three-byte requests: the little-endian ushort port, then the value byte.</param>
    /// <param name="failure">The message reported when a dispatch fails; its completion is unknown.</param>
    internal void WritePortBytes(ReadOnlySpan<byte[]> requests, string failure)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _driver.WritePortBytes(requests, failure);
    }

    /// <summary>Maps physical memory for one access under the gate and releases the mapping afterwards.</summary>
    /// <param name="address">Physical address the caller has already admitted.</param>
    /// <param name="bytes">Mapped length in bytes.</param>
    /// <param name="action">The access performed on the mapped pointer.</param>
    internal T Map<T>(uint address, uint bytes, Func<nint, T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _driver.Map(address, bytes, action);
    }

    /// <summary>One load of the pinned library and its driver handle, shared by every open handle.</summary>
    private sealed class Driver : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly FileStream _held;
        private readonly nint _library;
        private readonly MapMemory _map;
        private readonly SafeFileHandle _readiness;
        private readonly UnmapMemory _unmap;
        private bool _disposed;

        private Driver(string path, FileStream held, SafeFileHandle readiness, nint library)
        {
            Path = path;
            _held = held;
            _readiness = readiness;
            _library = library;
            _map = Export<MapMemory>("MapPhysToLin");
            _unmap = Export<UnmapMemory>("UnmapPhysicalMemory");
            _ = Export<WritePort>("DlPortWritePortUchar");
            if (!Export<DriverReady>("IsInpOutDriverOpen")())
            {
                throw new IOException("The InpOut driver did not open.");
            }
        }

        internal string Path { get; }

        /// <summary>The open handles over this load; guarded by <see cref="Drivers" />.</summary>
        internal int Handles { get; set; }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                NativeLibrary.Free(_library);
                _readiness.Dispose();
                _held.Dispose();
            }
        }

        internal static Driver Open(string path)
        {
            using var manifest =
                File.OpenRead(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "inpout.lock.json"));
            using var pin = JsonDocument.Parse(manifest);
            var digest = pin.RootElement.GetProperty("component").GetProperty("sha256").GetString()
                         ?? throw new InvalidDataException("The InpOut payload pin is missing.");
            var held = PinnedPayload.OpenHeld(path, digest);
            SafeFileHandle? readiness = null;
            nint library = 0;
            try
            {
                // InpOut's DLL entry point may install a missing driver. Require the existing device first.
                readiness = NativeDevice.CreateFile(@"\\.\inpoutx64", 0, 3, 0, 3, 0, 0);
                if (readiness.IsInvalid)
                {
                    throw new IOException(
                        "The pre-installed InpOut x64 driver is not ready. The library does not install it.");
                }

                library = NativeLibrary.Load(path);
                return new Driver(path, held, readiness, library);
            }
            catch
            {
                if (library != 0)
                {
                    NativeLibrary.Free(library);
                }

                readiness?.Dispose();
                held.Dispose();
                throw;
            }
        }

        internal void WritePortBytes(ReadOnlySpan<byte[]> requests, string failure)
        {
            Span<byte> output = stackalloc byte[10];
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                foreach (var request in requests)
                {
                    // The public void export silently discards IOCTL failure. The pinned driver's verified
                    // ABI is a ushort port followed by one byte; acknowledge the actual driver dispatch.
                    if (!NativeDevice.DeviceIoControl(_readiness, 0x9C402008, request.AsSpan(0, 3), output, out _))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), failure);
                    }
                }
            }
        }

        internal T Map<T>(uint address, uint bytes, Func<nint, T> action)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var pointer = _map(new nint(address), bytes, out var mapping);
                if (pointer == 0 || mapping == 0)
                {
                    // The driver can return a section handle without a view after translation fails.
                    if (mapping != 0)
                    {
                        _unmap(mapping, pointer);
                    }

                    throw new IOException("InpOut did not return a physical-memory mapping.");
                }

                try
                {
                    return action(pointer);
                }
                finally
                {
                    if (!_unmap(mapping, pointer))
                    {
                        throw new IOException("The physical-memory mapping could not be released.");
                    }
                }
            }
        }

        private T Export<T>(string name) where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint MapMemory(nint address, uint bytes, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UnmapMemory(nint handle, nint pointer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WritePort(ushort port, byte value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool DriverReady();
}
