// Native fixtures share the active device diagnostic sink and process-wide transport seams.
// Keep each owning lifetime isolated, including its background readers and observation loop.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LibHandheld.Tests;

internal static class NoLiveIntelPolicy
{
    // Intel power fixtures must never change the real IPF/DTT services of the machine running the tests.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Install()
    {
        LibHandheld.Internal.Devices.IntelPowerPaths.PolicyControl = new AbsentIntelPolicy();
    }

    private sealed class AbsentIntelPolicy : LibHandheld.Internal.Devices.IIntelPolicyControl
    {
        public LibHandheld.Internal.Windows.WindowsServiceState? Read(string name) => null;
        public bool IsRunning(string name) => false;
        public void DisableAndStop(string name) { }
        public void Stop(string name) { }
        public void Restore(string name, LibHandheld.Internal.Windows.WindowsServiceState original) { }
    }
}
