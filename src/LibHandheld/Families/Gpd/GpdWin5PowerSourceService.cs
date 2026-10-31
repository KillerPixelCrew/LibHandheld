using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Gpd;

/// <summary>WIN 5 exposes its Type-C-only power source through indexed EC register 0x0577.</summary>
internal sealed class GpdWin5PowerSourceService(Func<IEmbeddedController>? open = null)
    : ProfiledFeatureService("gpd-win5-power-source")
{
    internal const string CapabilityId = "power.type-c-only";

    private static readonly IReadOnlyList<CapabilityDescriptor> Controls =
    [
        CapabilityDescriptors.Boolean(CapabilityId, CapabilityRole.GenericReadOnly,
            CapabilityDescriptors.Label("Type-C power only"), read: true, write: false)
    ];

    private readonly ObservedValues _observed = new();
    private IEmbeddedController? _ec;
    private CapabilityReason? _readFailure;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => Controls;

    internal override IReadOnlyList<CapabilityState> States =>
    [
        ProviderControls.State(CapabilityId, _ec is not null, _observed.Value(CapabilityId),
            _observed.Time(CapabilityId), _readFailure ?? Reason)
    ];

    public override bool Suspendable => true;

    public override ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ec = open is null ? PawnIoEmbeddedController.OpenIndexed(0x4E, 0x4F) : open();
        Observe(token);
        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        Observe(token);
        return ValueTask.CompletedTask;
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
            "The WIN 5 power source is a read-only observation."));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        await DisposeAsync().ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    internal override ValueTask DisposeAsync()
    {
        _ec?.Dispose();
        _ec = null;
        _observed.Clear();
        _readFailure = null;
        return ValueTask.CompletedTask;
    }

    private void Observe(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ec is null)
        {
            return;
        }

        try
        {
            var value = _ec.ReadByte(0x0577);
            _observed.Read(CapabilityId, CapabilityValue.Boolean(value is 0x02 or 0x10));
            _readFailure = null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
        {
            _observed.Forget(CapabilityId);
            _readFailure = new CapabilityReason(CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }
}
