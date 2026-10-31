using System.ComponentModel;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

// Periodic observation polls sensors only; acknowledged control writes stay authoritative.
internal sealed partial class AllyDeviceEngine
{
    /// <remarks>
    ///     Only the fan readings are sampled here, so only they are published. Service state changed by a
    ///     transport callback is published by that callback.
    /// </remarks>
    internal override async ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        await RefreshObservedForCapabilityAsync(CapabilityIds.FanReading, token).ConfigureAwait(false);
        PublishStates(CapabilityIds.FanReading, null);
    }

    /// <summary>Refreshes read-only fan telemetry without querying control values.</summary>
    private ValueTask RefreshObservedForCapabilityAsync(string? capabilityId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (_fans is { State: DeviceServiceState.Owned } && capabilityId is null or CapabilityIds.FanReading)
            {
                _fans.RefreshTelemetry();
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            DeviceTrace.Change("observe", "acpi", DiagnosticText.FromException("ATKACPI telemetry read failed", ex));
        }

        return ValueTask.CompletedTask;
    }
}
