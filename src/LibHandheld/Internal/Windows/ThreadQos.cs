using System.Runtime.InteropServices;

namespace LibHandheld.Internal.Windows;

/// <summary>Marks the calling thread as latency-sensitive.</summary>
internal static partial class ThreadQos
{
    private const int ThreadPowerThrottlingClass = 3;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 1;

    /// <summary>Tags the thread HighQoS, so it keeps full speed when its host runs the process on EcoQoS.</summary>
    /// <remarks>Best effort: a Windows build that refuses the tag leaves the thread as it was.</remarks>
    internal static void UseHigh()
    {
        var state = new State { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
        _ = SetThreadInformation(GetCurrentThread(), ThreadPowerThrottlingClass, ref state,
            (uint)Marshal.SizeOf<State>());
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint thread, int informationClass, ref State information,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
}
