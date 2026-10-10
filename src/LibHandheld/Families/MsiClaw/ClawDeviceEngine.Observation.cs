using LibHandheld.Contracts;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

// Live sensor readings and the independent power watchdog; controls publish accepted write intent.
internal sealed partial class ClawDeviceEngine
{
    internal override async ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        if (_telemetry is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_telemetry, _telemetry.RefreshAsync, token).ConfigureAwait(false);
            await PublishCapabilityStatesAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>Post-command publication clears prior write faults; the periodic pass runs watchdog and sensors.</summary>
    private ValueTask RefreshObservedForCapabilityAsync(string? capabilityId, CancellationToken cancellationToken)
    {
        return capabilityId is null
            ? RefreshAllObservedAsync(cancellationToken)
            : RefreshObservedAsync(capabilityId, cancellationToken);
    }

    /// <summary>Runs the ten-second watchdog without querying control state, then refreshes sensor readings.</summary>
    private async ValueTask RefreshAllObservedAsync(CancellationToken cancellationToken)
    {
        if (_power is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_power, _powerCapability!.ReassertAsync, cancellationToken)
                .ConfigureAwait(false);
        }

        if (_telemetry is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_telemetry, _telemetry.RefreshAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RefreshServiceAsync(DeviceServiceStatus service,
        Func<CancellationToken, ValueTask> refresh, CancellationToken cancellationToken)
    {
        try
        {
            await refresh(cancellationToken).ConfigureAwait(false);
            _observationFailures.Remove(service.ServiceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var detail = DiagnosticText.FromException("Observation unavailable", ex);
            _observationFailures[service.ServiceId] =
                new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail);
            DeviceTrace.Change("observe", service.ServiceId, detail);
        }
    }

    private ValueTask RefreshObservedAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ServiceForCapability(capabilityId) is { } service)
        {
            _observationFailures.Remove(service.ServiceId);
        }

        return ValueTask.CompletedTask;
    }
}
