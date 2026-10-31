using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

// Live sensor readings and the TDP watchdog; controls publish accepted write intent.
internal sealed partial class ClawDeviceEngine
{
    /// <remarks>
    ///     Only the fan and temperature readings are sampled here, so only they are published. Service state
    ///     changed by a transport callback is published by that callback.
    /// </remarks>
    internal override async ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        if (_telemetry is { State: DeviceServiceState.Owned } telemetry)
        {
            await RefreshTelemetryReadingAsync(telemetry, token).ConfigureAwait(false);
            PublishStates(telemetry);
        }
    }

    /// <summary>
    ///     The periodic pass runs the ten-second TDP watchdog and refreshes the sensors; the publication after
    ///     a command reads nothing.
    /// </summary>
    private async ValueTask RefreshObservedForCapabilityAsync(string? capabilityId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (capabilityId is not null)
        {
            return;
        }

        if (_power is { State: DeviceServiceState.Owned } && _powerCapability is { } power)
        {
            try
            {
                await power.ReassertAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The write's effect is unknown and the watchdog is disarmed; it is not written again.
                _observedValues.Forget(CapabilityIds.PowerSustained);
                _observedValues.Forget(CapabilityIds.PowerBoost);
                DeviceTrace.Change("observe", ClawServiceIds.Power,
                    DiagnosticText.FromException("The TDP watchdog write failed", ex));
            }
        }

        if (_telemetry is { State: DeviceServiceState.Owned } telemetry)
        {
            await RefreshTelemetryReadingAsync(telemetry, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the sensors once; a failed read leaves them unknown until the next one succeeds.</summary>
    private async ValueTask RefreshTelemetryReadingAsync(TelemetryService telemetry,
        CancellationToken cancellationToken)
    {
        try
        {
            await telemetry.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Change("observe", telemetry.ServiceId,
                DiagnosticText.FromException("Telemetry unavailable", ex));
        }

        ObserveTelemetry();
    }

    /// <summary>Records the telemetry service's last reading, or its absence, as the observed sensor values.</summary>
    private void ObserveTelemetry()
    {
        var reading = _telemetry?.LastTelemetry;
        var at = DateTimeOffset.UtcNow;
        _observedValues.Read(CapabilityIds.FanRpm, Integer(reading?.LeftRpm), CapabilityInstances.Left, at);
        _observedValues.Read(CapabilityIds.FanRpm, Integer(reading?.RightRpm), CapabilityInstances.Right, at);
        _observedValues.Read(CapabilityIds.Temperature, Integer(reading?.TemperatureCelsius), at: at);

        static CapabilityValue? Integer(int? value)
        {
            return value is { } integer ? CapabilityValue.Integer(integer) : null;
        }
    }
}
