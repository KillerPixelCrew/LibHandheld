using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace LibHandheld.Families.Generic;

/// <summary>Bind a selector range to the exact identity before any native resource is opened.</summary>
internal abstract class ProfiledEngineSelector(HandheldDefinition definition) : IDeviceEngine
{
    private ProfiledDeviceEngine? _engine;

    private ProfiledDeviceEngine Engine =>
        _engine ?? throw new InvalidOperationException("Select an exact current device before acquisition.");

    public string PackageId => definition.FamilyId;

    public async ValueTask<PluginDetectionResult> DetectAsync(PluginDetectionContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var profile = Select(context.Identity);
        if (profile is null || profile.Definition.Id != definition.Id)
        {
            return new PluginDetectionResult { Matched = false };
        }

        _engine = new ProfiledDeviceEngine(profile);
        return await _engine.DetectAsync(context, token).ConfigureAwait(false);
    }

    public ValueTask<PluginStartResult> StartAsync(PluginStartContext context, CancellationToken token)
    {
        return Engine.StartAsync(context, token);
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        return Engine.ExecuteCommandAsync(command, token);
    }

    public ValueTask SuspendAsync(PluginQuiesceContext context, CancellationToken token)
    {
        return Engine.SuspendAsync(context, token);
    }

    public ValueTask<PluginStartResult> ResumeAsync(PluginResumeContext context, CancellationToken token)
    {
        return Engine.ResumeAsync(context, token);
    }

    public ValueTask ReleaseControllerAsync(PluginControllerReleaseContext context, CancellationToken token)
    {
        return Engine.ReleaseControllerAsync(context, token);
    }

    public ValueTask SetControllerManagementAsync(PluginControllerManagementContext context, CancellationToken token)
    {
        return Engine.SetControllerManagementAsync(context, token);
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        return Engine.ApplyHapticOutputAsync(frame, token);
    }

    public ValueTask<PluginDiagnostics> GetDiagnosticsAsync(CancellationToken token)
    {
        return Engine.GetDiagnosticsAsync(token);
    }

    public ValueTask<PluginStopResult> StopAsync(PluginStopContext context, CancellationToken token)
    {
        return _engine is null
            ? ValueTask.FromResult(new PluginStopResult { Status = PluginStopStatus.Clean })
            : _engine.StopAsync(context, token);
    }

    public ValueTask DisposeAsync()
    {
        return _engine?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    protected abstract ProfiledDeviceModel? Select(DeviceIdentitySnapshot identity);
}
