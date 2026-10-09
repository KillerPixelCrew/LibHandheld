using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

// What the periodic and post-command refresh re-reads; DeviceCommandSerializer runs both.
internal sealed partial class ClawDeviceEngine
{
    /// <summary>The serializer's refresh: one capability's services, or every owned service when the id is null.</summary>
    private ValueTask RefreshAsync(string? capabilityId, CancellationToken cancellationToken)
    {
        return capabilityId is null
            ? RefreshAllObservedAsync(cancellationToken)
            : RefreshObservedAsync(capabilityId, cancellationToken);
    }

    /// <summary>Re-reads every observable service that is currently owned.</summary>
    private async ValueTask RefreshAllObservedAsync(CancellationToken cancellationToken)
    {
        if (_power is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_power, token => _power.RefreshAsync(token, true), cancellationToken)
                .ConfigureAwait(false);
        }

        if (_chargeLimit is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_chargeLimit, _chargeLimit.RefreshAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_fans is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_fans, _fans.RefreshAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_telemetry is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_telemetry, _telemetry.RefreshAsync, cancellationToken).ConfigureAwait(false);
        }

        if (_lighting is { State: DeviceServiceState.Owned })
        {
            await RefreshServiceAsync(_lighting, _lighting.RefreshAsync, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask RefreshObservedAsync(
        string capabilityId,
        CancellationToken cancellationToken)
    {
        switch (capabilityId)
        {
            case CapabilityIds.PowerSustained or CapabilityIds.PowerBoost or CapabilityIds.Scenario:
                await _power!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                _observationFailures.Remove(_power.ServiceId);
                break;
            case CapabilityIds.ChargeLimit:
                await _chargeLimit!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                _observationFailures.Remove(_chargeLimit.ServiceId);
                break;
            case CapabilityIds.FanMode or CapabilityIds.FanCurve:
                await _fans!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                _observationFailures.Remove(_fans.ServiceId);
                await _telemetry!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                _observationFailures.Remove(_telemetry.ServiceId);
                break;
            case CapabilityIds.LightingBrightness or CapabilityIds.LightingColor:
                await _lighting!.RefreshAsync(cancellationToken).ConfigureAwait(false);
                _observationFailures.Remove(_lighting.ServiceId);
                break;
        }
    }
}
