using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace LibHandheld.Families.SteamDeck;

/// <summary>The model selects transports; shared services own power pairing, commands and cycle sequencing.</summary>
internal sealed class SteamDeckDeviceEngine : IDeviceEngine
{
    private readonly ProfiledDeviceEngine _engine;
    private SteamDeckNativeService? _native;

    internal SteamDeckDeviceEngine(HandheldDefinition definition)
    {
        var profile = SteamDeckModels.Profile(definition) with
        {
            CreateNativeService = context => _native = new SteamDeckNativeService(context, NeptuneDevice.Open),
            OemControls = Array.AsReadOnly(new[] { "steam", "quick-access", "l4", "r4", "l5", "r5" }.Select(id =>
                new OemControlDescriptor
                {
                    ControlId = id, Display = new CapabilityDisplay
                    {
                        Key = DisplayKey.Custom, CustomLabel = id switch
                        {
                            "steam" => "Steam", "quick-access" => "Quick Access", _ => id.ToUpperInvariant()
                        }
                    },
                    Placement = id is "steam" or "quick-access" ? OemControlPlacement.Front : OemControlPlacement.Rear,
                    RequiresControllerAcquisition = true
                }).ToArray())
        };
        _engine = new ProfiledDeviceEngine(profile, VangoghPower.Open,
            _ => throw new NotSupportedException("Valve cooling uses its firmware MMIO interface."));
    }

    public string PackageId => _engine.PackageId;

    public ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context, CancellationToken token)
    {
        return _engine.DetectAsync(context, token);
    }

    public ValueTask<PluginStartResult> StartAsync(PluginStartContext context, CancellationToken token)
    {
        return _engine.StartAsync(context, token);
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        return _engine.ExecuteCommandAsync(command, token);
    }

    public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken token)
    {
        return _engine.SuspendAsync(context, token);
    }

    public ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context, CancellationToken token)
    {
        return _engine.ResumeAsync(context, token);
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken token)
    {
        return _engine.GetDiagnosticsAsync(token);
    }

    public ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context, CancellationToken token)
    {
        return _engine.ReleaseControllerAsync(context, token);
    }

    public ValueTask SetControllerManagementAsync(PluginControllerManagementContext context, CancellationToken token)
    {
        return _engine.SetControllerManagementAsync(context, token);
    }

    public ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken token)
    {
        return _engine.StopAsync(context, token);
    }

    public ValueTask DisposeAsync()
    {
        return _engine.DisposeAsync();
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _native?.Rumble(frame);
        return ValueTask.CompletedTask;
    }
}
