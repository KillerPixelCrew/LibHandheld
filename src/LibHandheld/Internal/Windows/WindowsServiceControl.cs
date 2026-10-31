using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LibHandheld.Internal.Windows;

/// <summary>A Windows service's start type and whether it was running.</summary>
/// <param name="StartType">SERVICE_AUTO_START 2, DEMAND 3, DISABLED 4.</param>
/// <param name="Running">Whether the service was running or starting.</param>
internal readonly record struct WindowsServiceState(int StartType, bool Running);

/// <summary>Minimal Service Control Manager access for keeping a vendor service off and restoring it.</summary>
/// <remarks>Requires administrator rights, which WSGM's hardware owner already holds.</remarks>
internal static partial class WindowsServiceControl
{
    internal const int Disabled = 4;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ControlStop = 0x00000001;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;

    /// <summary>Reads the service, or null when it is not installed.</summary>
    internal static WindowsServiceState? Read(string name)
    {
        using var service = Open(name, ServiceQueryConfig | ServiceQueryStatus);
        return service is null ? null : new WindowsServiceState(StartType(service), IsRunning(service));
    }

    /// <summary>Whether the installed service is running or starting.</summary>
    internal static bool IsRunning(string name)
    {
        using var service = Open(name, ServiceQueryStatus);
        return service is not null && IsRunning(service);
    }

    /// <summary>Disables the start type, so neither boot nor failure recovery restarts it, then stops it.</summary>
    internal static void DisableAndStop(string name)
    {
        using var service = Open(name, ServiceChangeConfig | ServiceStop | ServiceQueryStatus)
                            ?? throw new InvalidOperationException($"Service {name} is not installed.");
        SetStartType(service, Disabled);
        Stop(service);
    }

    /// <summary>Stops the service again without changing its configuration.</summary>
    internal static void Stop(string name)
    {
        using var service = Open(name, ServiceStop | ServiceQueryStatus);
        if (service is not null)
        {
            Stop(service);
        }
    }

    /// <summary>Restores the start type and starts the service when it was running.</summary>
    internal static void Restore(string name, WindowsServiceState original)
    {
        using var service = Open(name, ServiceChangeConfig | ServiceStart | ServiceQueryStatus);
        if (service is null)
        {
            return;
        }

        SetStartType(service, original.StartType);
        if (original.Running && !StartServiceW(service, 0, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceAlreadyRunning)
            {
                throw new Win32Exception(error, $"Service {name} could not be started.");
            }
        }
    }

    private static Handle? Open(string name, uint access)
    {
        using var manager = new Handle(OpenSCManagerW(null, null, ScManagerConnect));
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The Service Control Manager could not be opened.");
        }

        var service = new Handle(OpenServiceW(manager, name, access));
        if (!service.IsInvalid)
        {
            return service;
        }

        var error = Marshal.GetLastPInvokeError();
        service.Dispose();
        return error == ErrorServiceDoesNotExist ? null : throw new Win32Exception(error, $"Service {name} could not be opened.");
    }

    private static int StartType(Handle service)
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The service configuration could not be read.");
            }

            // QUERY_SERVICE_CONFIGW: dwServiceType, then dwStartType.
            return Marshal.ReadInt32(buffer, 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool IsRunning(Handle service)
    {
        if (!QueryServiceStatus(service, out var status))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The service status could not be read.");
        }

        // SERVICE_STOPPED is 1; every other state means it is running or changing state.
        return status.CurrentState != 1;
    }

    private static void SetStartType(Handle service, int startType)
    {
        if (!ChangeServiceConfigW(service, ServiceNoChange, (uint)startType, ServiceNoChange, null, null, IntPtr.Zero,
                null, null, null, null))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The service start type could not be changed.");
        }
    }

    private static void Stop(Handle service)
    {
        if (!ControlService(service, ControlStop, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorServiceNotActive)
            {
                throw new Win32Exception(error, "The service could not be stopped.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private sealed class Handle : SafeHandle
    {
        internal Handle(IntPtr value) : base(IntPtr.Zero, true)
        {
            SetHandle(value);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            return CloseServiceHandle(handle);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    private static partial IntPtr OpenServiceW(SafeHandle manager, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(SafeHandle service, IntPtr config, uint size, out uint needed);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(SafeHandle service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(SafeHandle service, uint type, uint startType, uint errorControl,
        string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password,
        string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(SafeHandle service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(SafeHandle service, uint count, IntPtr arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
