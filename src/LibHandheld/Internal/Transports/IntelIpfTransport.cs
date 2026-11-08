using System.Runtime.InteropServices;
using System.Text;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Transports;

/// <summary>IPF refused a session's first request as naming unknown handles, so nothing was applied.</summary>
internal sealed class IntelIpfStaleSessionException(string message) : IOException(message);

/// <summary>An open IPF client session that requests PL1/PL2; disposing it releases the request.</summary>
internal interface IIntelIpfSession : IDisposable
{
    /// <summary>Whether the server still holds the session: its pump is running and it has not destroyed the app.</summary>
    bool Alive { get; }

    /// <summary>Reads PL1/PL2 as IPF reports them, in watts.</summary>
    CpuPowerLimits ReadLimits();

    /// <summary>Requests PL1/PL2 in watts; sent once, never retried.</summary>
    /// <exception cref="IntelIpfStaleSessionException">IPF refused the first request, so nothing was applied.</exception>
    void WriteLimits(int sustainedWatts, int boostWatts);
}

/// <summary>
///     Requests the CPU RAPL power limits from Intel's Innovation Platform Framework (IPF) over its elevated
///     IPC session. IPF applies them itself through its Intel-signed driver (it writes the MCHBAR mirror), so
///     Memory Integrity can stay on and IPF has no competing value of its own to undo.
/// </summary>
/// <remarks>
///     IPC mode of ipfipc.dll, as Intel's ipf_apploader drives it (github.com/intel/dptf, Apache-2.0):
///     GetIpcInterface -> Ipc_Init -> Ipc_SetAppIface -> IpcSession_Create("pipe://ipfsrv.elevated") ->
///     IpcSession_Connect, then IpcSession_WaitForStop on its own thread as the message pump. The server then
///     calls AppCreate (handing over the primitive function) and ParticipantCreate/DomainCreate; the TCPU
///     participant's first domain is the package (PKG). Limits go through SET_RAPL_POWER_LIMIT (130) with the
///     instance selecting PL1 (0) or PL2 (1), as DPTF does. The elevated role may set but not clear
///     (SET_RAPL_POWER_LIMIT_CLEAR returns ESIF_E_SESSION_PERMISSION_DENIED), and IPF drops a client's request
///     when its session closes, so disconnecting is the release. IPF arbitrates RAPL limits across clients and
///     the lowest request wins (esif_uf_arbmgr.c). Verified end to end on an Atlas PA08-I51 with IPF 2.3
///     (2026-11-01): read 45/65 W, a 30/50 W request reached MCHBAR, and disconnect released it. The full
///     interface (structures with offsets, callbacks, call order, primitives, errors) is in docs/intel-ipf.md.
/// </remarks>
internal sealed unsafe class IntelIpfTransport : IIntelIpfSession
{
    private const int EsifOk = 0;

    // ESIF_E_INVALID_HANDLE: IPF no longer knows the app, participant or domain the request names.
    private const int EsifInvalidHandle = 1004;
    private const int GetRaplPowerLimit = 38;
    private const int SetRaplPowerLimit = 130;
    private const int SetRaplPowerLimitEnable = 222;
    private const uint EsifDataUint32 = 3;
    private const uint EsifDataString = 8;
    private const uint EsifDataPower = 26;
    private const byte Pl1 = 0;
    private const byte Pl2 = 1;
    private const string CpuParticipant = "TCPU";
    private const string ElevatedServer = "pipe://ipfsrv.elevated";

    // IPF_SDK_VERSION from Intel's public ipf_sdk_version.h; any 1.0.x not newer than the server is accepted.
    private const string ClientSdkVersion = "1.0.11402";

    // IpcInterface (pack 1): hdr 8 + 3 x char[20] + 8 function pointers.
    private const int IpcInterfaceSize = 132;
    private const int IpcFunctions = 68;

    // AppInterfaceSet (pack 1): hdr 8 + AppInterface 15 pointers + EsifInterface 8 pointers.
    private const int AppInterfaceSetSize = 192;
    private const int SessionInfoSize = 4 + 64 + 20 + 64 + 1024 + 260 + 20;

    private readonly Lock _gate = new();
    private readonly List<Delegate> _rooted = [];

    // IPF ends a connection whose app name is still registered, and a closing session's name stays
    // registered until its asynchronous stop has run, so every connection gets its own name.
    private static int _connectionCount;
    private readonly string _appName = $"WSGM{Environment.ProcessId:X}N{Interlocked.Increment(ref _connectionCount)}";
    private IntPtr _library;
    private byte* _iface;
    private byte* _set;
    private byte* _info;
    private ulong _session = ulong.MaxValue;
    private bool _initialized;
    private bool _connected;
    private Thread? _pump;
    private bool _disposed;

    // Captured from the server's callbacks on the IPC thread.
    private volatile bool _nameAsked;
    private volatile bool _created;
    private volatile bool _destroyed;
    private volatile bool _open;
    private ulong _esifHandle;
    private IntPtr _primitive;
    private ulong _cpu;
    private ulong _cpuDomain;

    private IntelIpfTransport()
    {
    }

    /// <summary>Whether Intel's IPF IPC library is installed; nothing is loaded or run.</summary>
    internal static bool Present()
    {
        return ResolveIpc() is not null;
    }

    public bool Alive => _pump is { IsAlive: true } && !_destroyed;

    /// <summary>Opens the elevated IPF session and waits for the CPU participant; ipfsvc must be running.</summary>
    /// <param name="token">Ends the wait for the server's callbacks, so a caller's deadline holds.</param>
    internal static IntelIpfTransport Open(CancellationToken token)
    {
        var transport = new IntelIpfTransport();
        try
        {
            transport.Connect(token);
            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>Reads PL1/PL2 as IPF reports them, in watts.</summary>
    public CpuPowerLimits ReadLimits()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sustained = Get(Pl1) / 1000.0;
            var boost = Get(Pl2) / 1000.0;
            return new CpuPowerLimits(sustained, boost, boost);
        }
    }

    /// <summary>Enables PL1 and PL2 and requests both, PL2 first; sent once, never retried.</summary>
    public void WriteLimits(int sustainedWatts, int boostWatts)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var first = Send(SetRaplPowerLimitEnable, 1, EsifDataUint32, Pl1);
            if (first == EsifInvalidHandle || Volatile.Read(ref _cpu) == 0 || Volatile.Read(ref _cpuDomain) == 0)
            {
                throw new IntelIpfStaleSessionException($"IPF no longer knows this session's handles (ESIF error 0x{first:X}).");
            }

            Check(first, $"primitive {SetRaplPowerLimitEnable}/{Pl1}");
            Set(SetRaplPowerLimitEnable, 1, EsifDataUint32, Pl2);
            Set(SetRaplPowerLimit, checked((uint)boostWatts * 1000), EsifDataPower, Pl2);
            Set(SetRaplPowerLimit, checked((uint)sustainedWatts * 1000), EsifDataPower, Pl1);
        }
    }

    /// <summary>Disconnects, which makes IPF drop this client's request, then releases the library.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                var fn = _iface is null ? null : (IntPtr*)(_iface + IpcFunctions);
                if (_connected && fn is not null)
                {
                    ((delegate* unmanaged[Cdecl]<ulong, void>)fn[6])(_session);
                    _pump?.Join(TimeSpan.FromSeconds(5));
                }

                if (_session != ulong.MaxValue && _session != 0 && fn is not null)
                {
                    ((delegate* unmanaged[Cdecl]<ulong, void>)fn[4])(_session);
                }

                if (_initialized && fn is not null)
                {
                    ((delegate* unmanaged[Cdecl]<void>)fn[1])();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                DeviceTrace.Warn("ipf", $"IPF session teardown failed: {exception.Message}");
            }

            NativeMemory.Free(_info);
            NativeMemory.Free(_set);
            NativeMemory.Free(_iface);
            _info = null;
            _set = null;
            _iface = null;
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
                _library = IntPtr.Zero;
            }

            _rooted.Clear();
        }
    }

    private void Connect(CancellationToken token)
    {
        var path = ResolveIpc() ?? throw new IOException("Intel IPF (ipfipc.dll) is not installed.");
        _library = NativeLibrary.Load(path);

        _iface = (byte*)NativeMemory.AllocZeroed(256);
        *(uint*)_iface = 8; // eIfaceTypeIpfIpc
        *(ushort*)(_iface + 4) = 1; // IPC_INTERFACE_VERSION
        *(ushort*)(_iface + 6) = IpcInterfaceSize;
        Put(_iface + 48, ClientSdkVersion); // Ipc_ClientSdkVersion
        var getInterface = (delegate* unmanaged[Cdecl]<byte*, int>)NativeLibrary.GetExport(_library, "GetIpcInterface");
        Check(getInterface(_iface), "GetIpcInterface");

        var fn = (IntPtr*)(_iface + IpcFunctions);
        Check(((delegate* unmanaged[Cdecl]<int>)fn[0])(), "Ipc_Init");
        _initialized = true;

        BuildAppInterfaceSet();
        Check(((delegate* unmanaged[Cdecl]<byte*, int>)fn[2])(_set), "Ipc_SetAppIface");

        _info = (byte*)NativeMemory.AllocZeroed(SessionInfoSize);
        *(uint*)_info = 1;
        Put(_info + 4, _appName);
        Put(_info + 4 + 64, "1.0.0");
        Put(_info + 4 + 64 + 20, "WSGM power limits");
        Put(_info + 4 + 64 + 20 + 64 + 1024, ElevatedServer);
        _session = ((delegate* unmanaged[Cdecl]<byte*, ulong>)fn[3])(_info);
        if (_session == ulong.MaxValue || _session == 0)
        {
            throw new IOException("IPF did not create a session for its elevated server.");
        }

        Check(((delegate* unmanaged[Cdecl]<ulong, int>)fn[5])(_session), "IpcSession_Connect");
        _connected = true;

        // IpcSession_WaitForStop is the message pump that dispatches the server's App calls until the session
        // closes; Intel's loader runs it right after Connect.
        var session = _session;
        var waitForStop = fn[7];
        _pump = new Thread(() => ((delegate* unmanaged[Cdecl]<ulong, void>)waitForStop)(session))
        {
            IsBackground = true, Name = "WSGM IPF session"
        };
        _pump.Start();

        // The server introduces the app and its participants within about 0.1 s on the Atlas.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(_created && Volatile.Read(ref _cpu) != 0 && Volatile.Read(ref _cpuDomain) != 0)
               && _pump.IsAlive && DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(25);
        }

        if (!_created || _primitive == IntPtr.Zero)
        {
            throw new IOException("IPF connected but never created the WSGM app session "
                                  + $"(asked the name: {_nameAsked}, session closed: {!_pump.IsAlive}).");
        }

        if (Volatile.Read(ref _cpu) == 0 || Volatile.Read(ref _cpuDomain) == 0)
        {
            throw new IOException("IPF did not report its TCPU participant.");
        }

        _open = true;
        DeviceTrace.Change("ipf", "session", $"IPF elevated session open; TCPU {_cpu}, domain {_cpuDomain}.");
    }

    private uint Get(byte instance)
    {
        var value = 0u;
        var response = new EsifData { Type = EsifDataPower, Buf = (IntPtr)(&value), BufLen = sizeof(uint) };
        var request = default(EsifData);
        Check(Primitive(&request, &response, GetRaplPowerLimit, instance), $"GET_RAPL_POWER_LIMIT/{instance}");
        return value;
    }

    private void Set(int primitive, uint value, uint type, byte instance)
    {
        Check(Send(primitive, value, type, instance), $"primitive {primitive}/{instance}");
    }

    private int Send(int primitive, uint value, uint type, byte instance)
    {
        var data = value;
        var request = new EsifData { Type = type, Buf = (IntPtr)(&data), BufLen = sizeof(uint), DataLen = sizeof(uint) };
        var response = default(EsifData);
        return Primitive(&request, &response, primitive, instance);
    }

    private int Primitive(EsifData* request, EsifData* response, int primitive, byte instance)
    {
        var call = (delegate* unmanaged[Cdecl]<ulong, ulong, ulong, EsifData*, EsifData*, int, byte, int>)_primitive;
        return call(_esifHandle, Volatile.Read(ref _cpu), Volatile.Read(ref _cpuDomain), request, response, primitive, instance);
    }

    /// <summary>Fills the 15 AppInterface callbacks; the EsifInterface half is the server's.</summary>
    private void BuildAppInterfaceSet()
    {
        _set = (byte*)NativeMemory.AllocZeroed(AppInterfaceSetSize);
        *(uint*)_set = 0; // eIfaceTypeApplication
        *(ushort*)(_set + 4) = 4; // APP_INTERFACE_VERSION: 64-bit handles
        *(ushort*)(_set + 6) = AppInterfaceSetSize;
        var app = (IntPtr*)(_set + 8);
        app[0] = Root<AppCreateDelegate>(OnCreate);
        app[1] = Root<HandleDelegate>(_ => OnDestroy());
        app[2] = Root<HandleDelegate>(_ => OnPowerNotice("suspended"));
        app[3] = Root<HandleDelegate>(_ => OnPowerNotice("resumed"));
        app[4] = Root<StringDelegate>(data => WriteString(data, "1.0.0"));
        app[5] = Root<CommandDelegate>((_, _, _, _) => EsifOk);
        app[6] = Root<IntroDelegate>((_, data) => WriteString(data, _appName));
        app[7] = Root<StringDelegate>(data => WriteString(data, "WSGM power limits"));
        app[8] = Root<StringDelegate>(OnGetName);
        app[9] = Root<StatusDelegate>((_, _, _, _) => EsifOk);
        app[10] = Root<ParticipantCreateDelegate>(OnParticipant);
        app[11] = Root<Handle2Delegate>(OnParticipantDestroy);
        app[12] = Root<DomainCreateDelegate>(OnDomain);
        app[13] = Root<Handle3Delegate>(OnDomainDestroy);
        app[14] = Root<EventDelegate>((_, _, _, _, _) => EsifOk);
    }

    private int OnGetName(EsifData* data)
    {
        _nameAsked = true;
        return WriteString(data, _appName);
    }

    private int OnDestroy()
    {
        _destroyed = true;
        if (_open)
        {
            DeviceTrace.Info("ipf", "IPF destroyed the WSGM app session.");
        }

        return EsifOk;
    }

    private static int OnPowerNotice(string notice)
    {
        DeviceTrace.Info("ipf", $"IPF {notice} the WSGM app session.");
        return EsifOk;
    }

    private int OnCreate(byte* set, ulong esifHandle, ulong* appHandle, IntPtr appData, int state)
    {
        _esifHandle = esifHandle;
        *appHandle = 0x5753474D; // 'WSGM'
        // EsifInterface follows the 15 AppInterface pointers; its third pointer is fPrimitiveFuncPtr.
        _primitive = ((IntPtr*)(set + 8 + (15 * 8)))[2];
        _created = true;
        return EsifOk;
    }

    private int OnParticipant(ulong app, ulong participant, IntPtr data, int state)
    {
        // AppParticipantData (pack 1): version + reserved (4), EsifData fDriverType, fDeviceType, fName.
        if (string.Equals(EsifString(data, 4 + (2 * sizeof(EsifData))), CpuParticipant, StringComparison.OrdinalIgnoreCase))
        {
            // The participant's domains follow with new handles, also when IPF creates it again after a standby.
            Volatile.Write(ref _cpuDomain, 0);
            Volatile.Write(ref _cpu, participant);
        }

        return EsifOk;
    }

    private int OnDomain(ulong app, ulong participant, ulong domain, IntPtr data, int state)
    {
        // The participant's first domain is its package domain (D0, "PKG" on TCPU).
        if (participant == Volatile.Read(ref _cpu) && Volatile.Read(ref _cpuDomain) == 0)
        {
            Volatile.Write(ref _cpuDomain, domain);
            if (_open)
            {
                DeviceTrace.Info("ipf", $"IPF created the TCPU domain again; domain {domain}.");
            }
        }

        return EsifOk;
    }

    private int OnParticipantDestroy(ulong app, ulong participant)
    {
        if (participant == Volatile.Read(ref _cpu))
        {
            Volatile.Write(ref _cpuDomain, 0);
            Volatile.Write(ref _cpu, 0);
            DeviceTrace.Info("ipf", "IPF destroyed the TCPU participant.");
        }

        return EsifOk;
    }

    private int OnDomainDestroy(ulong app, ulong participant, ulong domain)
    {
        if (domain == Volatile.Read(ref _cpuDomain))
        {
            Volatile.Write(ref _cpuDomain, 0);
            DeviceTrace.Info("ipf", "IPF destroyed the TCPU domain.");
        }

        return EsifOk;
    }

    private static string EsifString(IntPtr data, int offset)
    {
        if (data == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buf = Marshal.ReadIntPtr(data, offset + 4);
        return buf == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(buf) ?? string.Empty;
    }

    private static int WriteString(EsifData* data, string value)
    {
        if (data is null || data->Buf == IntPtr.Zero || data->BufLen == 0)
        {
            return EsifOk;
        }

        var bytes = Encoding.ASCII.GetBytes(value + '\0');
        var count = Math.Min(bytes.Length, (int)data->BufLen);
        Marshal.Copy(bytes, 0, data->Buf, count);
        data->DataLen = (uint)count;
        data->Type = EsifDataString;
        return EsifOk;
    }

    private static void Put(byte* target, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Marshal.Copy(bytes, 0, (IntPtr)target, bytes.Length);
    }

    private IntPtr Root<T>(T callback) where T : Delegate
    {
        _rooted.Add(callback);
        return Marshal.GetFunctionPointerForDelegate(callback);
    }

    private static void Check(int rc, string what)
    {
        if (rc != EsifOk)
        {
            throw new IOException($"{what} failed with ESIF error 0x{rc:X}.");
        }
    }

    private static string? ResolveIpc()
    {
        var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32",
            "DriverStore", "FileRepository");
        if (!Directory.Exists(store))
        {
            return null;
        }

        return Directory.EnumerateDirectories(store, "ipf_cpu.inf_*")
            .Select(directory => Path.Combine(directory, "ipfipc.dll"))
            .FirstOrDefault(File.Exists);
    }

    // esif_data is declared under #pragma pack(1): type@0, buf_ptr@4, buf_len@12, data_len@16 (20 bytes).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct EsifData
    {
        public uint Type;
        public IntPtr Buf;
        public uint BufLen;
        public uint DataLen;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AppCreateDelegate(byte* set, ulong esifHandle, ulong* appHandle, IntPtr appData, int state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HandleDelegate(ulong handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Handle2Delegate(ulong first, ulong second);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Handle3Delegate(ulong first, ulong second, ulong third);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StringDelegate(EsifData* data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IntroDelegate(ulong app, EsifData* data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CommandDelegate(ulong app, uint argc, EsifData* argv, EsifData* response);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StatusDelegate(ulong app, int command, uint input, EsifData* output);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ParticipantCreateDelegate(ulong app, ulong participant, IntPtr data, int state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DomainCreateDelegate(ulong app, ulong participant, ulong domain, IntPtr data, int state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EventDelegate(ulong app, ulong participant, ulong domain, EsifData* data, EsifData* guid);
}
