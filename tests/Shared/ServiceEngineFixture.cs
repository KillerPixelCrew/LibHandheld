using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Internal.Testing;

/// <summary>Runs one injected family provider through the shipped shared engine.</summary>
internal sealed class ServiceEngineFixture(Func<ProfiledFeatureContext, ProfiledFeatureService> createProvider)
    : NativeEngine
{
    private ProfiledDeviceEngine? _engine;

    private ProfiledDeviceEngine Engine =>
        _engine ?? throw new InvalidOperationException("The fixture has not started.");

    public ValueTask<EngineStartResult> StartAsync(EngineStartContext context, CancellationToken token)
    {
        var definition = HandheldDevice.Definitions.Single(item => item.Id == context.DeviceDefinitionId);
        DeviceTrace.Install(context.Host is TestPluginHostAdapter host ? host.Log : null);
        _engine ??= new ProfiledDeviceEngine(ProfiledDeviceModel.ForServices(definition, [createProvider]));
        return _engine.StartAsync(context, token);
    }

    public ValueTask<CapabilityCommandResult> ExecuteCommandAsync(CapabilityCommand command, CancellationToken token)
    {
        return Engine.ExecuteCommandAsync(command, token);
    }

    public ValueTask SuspendAsync(EngineQuiesceContext context, CancellationToken token)
    {
        return Engine.SuspendAsync(context, token);
    }

    public ValueTask<EngineStartResult> ResumeAsync(EngineResumeContext context, CancellationToken token)
    {
        return Engine.ResumeAsync(context, token);
    }

    public ValueTask<IReadOnlyDictionary<string, string>> GetDiagnosticsAsync(CancellationToken token)
    {
        return Engine.GetDiagnosticsAsync(token);
    }

    public ValueTask ApplyHapticOutputAsync(HapticOutputFrame frame, CancellationToken token)
    {
        return Engine.ApplyHapticOutputAsync(frame, token);
    }

    public ValueTask ReleaseControllerAsync(EngineControllerReleaseContext context, CancellationToken token)
    {
        return Engine.ReleaseControllerAsync(context, token);
    }

    public ValueTask SetControllerManagementAsync(EngineControllerManagementContext context, CancellationToken token)
    {
        return Engine.SetControllerManagementAsync(context, token);
    }

    public ValueTask<EngineStopResult> StopAsync(EngineStopContext context, CancellationToken token)
    {
        return Engine.StopAsync(context, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }

        DeviceTrace.Install(null);
    }
}
