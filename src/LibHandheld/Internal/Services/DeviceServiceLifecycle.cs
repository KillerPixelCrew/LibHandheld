using System.Text;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Plugin;

namespace LibHandheld.Internal.Services;

/// <summary>Runs a plugin's service operations and turns the services' states into lifecycle results.</summary>
/// <remarks>
///     Ordinary service exceptions become that service's fault result; out-of-memory failures propagate.
///     Caller cancellation propagates from acquisition and suspend, but release records an unverified
///     result and continues the release walk. The plugin chooses services and serializes lifecycle calls.
///     Acquisition follows declaration order; suspend and release use reverse order.
/// </remarks>
internal static class DeviceServiceLifecycle
{
    /// <summary>Acquires one service and applies its result.</summary>
    /// <param name="service">The service to acquire.</param>
    /// <param name="context">The cycle it is acquired for.</param>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>A task completing once the service's state is applied.</returns>
    /// <remarks>A result other than Owned, Passive, Degraded or Faulted faults the service.</remarks>
    public static async ValueTask AcquireAsync<TIdentity>(
        DeviceService<TIdentity> service,
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        cancellationToken.ThrowIfCancellationRequested();
        var result = await InvokeAsync(service, () => service.AcquireAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        service.ApplyResult(result.State is DeviceServiceState.Owned or DeviceServiceState.Passive
            or DeviceServiceState.Degraded or DeviceServiceState.Faulted
            ? result
            : new DeviceServiceResult(DeviceServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted, $"Acquisition returned invalid state {result.State}.")));
    }

    /// <summary>Suspends one service and applies its result as it stands.</summary>
    /// <param name="service">The service to suspend.</param>
    /// <param name="context">The cycle being suspended.</param>
    /// <param name="cancellationToken">Cancels the suspend.</param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>A task completing once the service's state is applied.</returns>
    public static async ValueTask SuspendAsync<TIdentity>(
        DeviceService<TIdentity> service,
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        cancellationToken.ThrowIfCancellationRequested();
        service.ApplyResult(await InvokeAsync(service, () => service.SuspendAsync(context, cancellationToken),
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Releases one service and applies its result.</summary>
    /// <param name="service">The service to release.</param>
    /// <param name="context">The cycle being released.</param>
    /// <param name="cancellationToken">Cancels the release; a cancelled release is reported unverified.</param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>A task completing once the service's state is applied.</returns>
    /// <remarks>A result other than Idle, ReleasedUnverified or Faulted is reported unverified.</remarks>
    public static async ValueTask ReleaseAsync<TIdentity>(
        DeviceService<TIdentity> service,
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        DeviceServiceResult result;
        try
        {
            result = await InvokeAsync(service, () => service.ReleaseAsync(context, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new DeviceServiceResult(DeviceServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.Quiescing, $"Release of service '{service.ServiceId}' exceeded its deadline."));
        }

        service.ApplyResult(result.State is DeviceServiceState.Idle or DeviceServiceState.ReleasedUnverified
            or DeviceServiceState.Faulted
            ? result
            : new DeviceServiceResult(DeviceServiceState.ReleasedUnverified, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted, $"Release returned invalid state {result.State}.")));
    }

    /// <summary>Acquires services one after another in the order given.</summary>
    /// <param name="services">The services to acquire, in start order.</param>
    /// <param name="context">The cycle they are acquired for.</param>
    /// <param name="cancellationToken">
    ///     Cancels the acquisition walk. The caller must invoke <see cref="RollBackStartAsync{TIdentity}" />
    ///     to unwind a partially acquired cycle; this method does not roll it back.
    /// </param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>A task completing once every service's state is applied.</returns>
    public static async ValueTask AcquireAllAsync<TIdentity>(
        IEnumerable<DeviceService<TIdentity>> services,
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var service in services)
        {
            await AcquireAsync(service, context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops every suspendable service for a suspend, in release order.</summary>
    /// <param name="services">Every service of the cycle, in start order.</param>
    /// <param name="context">The cycle being suspended.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>A task completing once every suspendable service's state is applied.</returns>
    public static async ValueTask SuspendAllAsync<TIdentity>(
        IReadOnlyList<DeviceService<TIdentity>> services,
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        for (var index = services.Count - 1; index >= 0; index--)
        {
            if (services[index].Suspendable)
            {
                await SuspendAsync(services[index], context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Releases every service in reverse start order.</summary>
    /// <param name="services">Every service of the cycle, in start order.</param>
    /// <param name="context">The cycle being released.</param>
    /// <param name="cancellationToken">
    ///     Passed to each release. Cancellation is recorded as unverified and does not skip later services.
    /// </param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>A task completing once every service's state is applied.</returns>
    public static async ValueTask ReleaseAllAsync<TIdentity>(
        IReadOnlyList<DeviceService<TIdentity>> services,
        DeviceCycleContext<TIdentity> context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        for (var index = services.Count - 1; index >= 0; index--)
        {
            await ReleaseAsync(services[index], context, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Undoes a start that failed part-way: releases every service and retracts what it published.</summary>
    /// <param name="services">Every service the start created, in start order.</param>
    /// <param name="context">The cycle being rolled back, with a fresh deadline.</param>
    /// <param name="host">The host the start published to, or null when it never got that far.</param>
    /// <param name="published">The descriptor set the start published, or null when it published none.</param>
    /// <typeparam name="TIdentity">The plugin's identity snapshot type.</typeparam>
    /// <returns>Completion after all releases and applicable retractions were attempted, including failed cleanup.</returns>
    /// <remarks>
    ///     The physical devices and OEM controls are replaced with empty sets and the descriptors with an
    ///     empty set of the next generation. A retraction the host refuses is traced and the rest still run.
    /// </remarks>
    public static async ValueTask RollBackStartAsync<TIdentity>(
        IReadOnlyList<DeviceService<TIdentity>> services,
        DeviceCycleContext<TIdentity> context,
        IDeviceSink? host,
        CapabilityDescriptorSet? published)
    {
        using var bounded = context.Deadline.CreateCancellationSource();
        await ReleaseAllAsync(services, context, bounded.Token).ConfigureAwait(false);
        if (host is null)
        {
            return;
        }

        await TryRetractAsync("physical devices",
            () => host.PublishPhysicalDevicesAsync([], null, bounded.Token)).ConfigureAwait(false);
        await TryRetractAsync("OEM controls",
            () => host.PublishOemControlsAsync([], bounded.Token)).ConfigureAwait(false);
        if (published is not null)
        {
            await TryRetractAsync("capability descriptors", () => host.PublishDescriptorsAsync(
                new CapabilityDescriptorSet
                {
                    Generation = checked(published.Generation + 1),
                    CycleGeneration = published.CycleGeneration,
                    Descriptors = []
                },
                bounded.Token)).ConfigureAwait(false);
        }
    }

    /// <summary>The aggregate start or resume result, traced with the state of every service.</summary>
    /// <param name="services">Every service of the cycle, in start order.</param>
    /// <param name="required">Whether a service counts toward the aggregate; a controller kept off does not.</param>
    /// <param name="noServiceDetail">The reason given when no required service is owned.</param>
    /// <param name="host">Receives the trace line; null skips it.</param>
    /// <returns>Active when every required service is owned, Passive when none is, otherwise Degraded.</returns>
    public static PluginStartResult StartResult(
        IReadOnlyList<DeviceServiceStatus> services,
        Func<DeviceServiceStatus, bool> required,
        string noServiceDetail,
        IDeviceSink? host)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(required);
        var counted = services.Where(required).ToArray();
        var owned = counted.Count(service => service.State is DeviceServiceState.Owned);
        var firstUnhealthy = counted.FirstOrDefault(service => service.State is not DeviceServiceState.Owned);
        PluginStartResult result = new()
        {
            State = owned == 0
                ? PluginOperationalState.Passive
                : firstUnhealthy is null
                    ? PluginOperationalState.Active
                    : PluginOperationalState.Degraded,
            Reason = firstUnhealthy?.Reason ?? (owned == 0
                ? new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing, noServiceDetail)
                : null)
        };

        // The aggregate carries only the first unhealthy service's reason, which cannot say which of the
        // others were fine. The trace lists all of them, every time.
        if (host is null)
        {
            return result;
        }

        StringBuilder detail = new();
        foreach (var service in services)
        {
            if (detail.Length > 0)
            {
                detail.Append(", ");
            }

            detail.Append(service.ServiceId).Append('=').Append(service.State);
            if (service.State is DeviceServiceState.Owned || service.Reason is not { } reason)
            {
                continue;
            }

            detail.Append('(').Append(reason.Code);
            if (!string.IsNullOrWhiteSpace(reason.Detail))
            {
                detail.Append(": ").Append(reason.Detail);
            }

            detail.Append(')');
        }

        host.Trace(
            result.State is PluginOperationalState.Active ? DeviceTraceLevel.Info : DeviceTraceLevel.Warn,
            "lifecycle",
            $"start state {result.State}: {detail}");
        return result;
    }

    /// <summary>The stop result: failed if any service faulted, unverified if any release was, else clean.</summary>
    /// <param name="services">Every service of the cycle.</param>
    /// <returns>The stop result.</returns>
    public static PluginStopResult StopResult(IReadOnlyList<DeviceServiceStatus> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.FirstOrDefault(service => service.State is DeviceServiceState.Faulted) is { } failed)
        {
            return new PluginStopResult
            {
                Status = PluginStopStatus.Failed,
                Reason = failed.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{failed.ServiceId}' cleanup failed.")
            };
        }

        return services.FirstOrDefault(service => service.State is DeviceServiceState.ReleasedUnverified) is
            { } unverified
            ? new PluginStopResult
            {
                Status = PluginStopStatus.Unverified,
                Reason = unverified.Reason ?? new CapabilityReason(
                    CapabilityReasonCode.TransportFaulted,
                    $"Service '{unverified.ServiceId}' cleanup was not verified.")
            }
            : new PluginStopResult { Status = PluginStopStatus.Clean };
    }

    /// <summary>The reason a capability state carries for a service that set none of its own.</summary>
    /// <param name="state">The service's state.</param>
    /// <returns>Null for an owned service, otherwise a reason code without detail.</returns>
    public static CapabilityReason? ReasonFor(DeviceServiceState state)
    {
        return state switch
        {
            DeviceServiceState.Owned => null,
            DeviceServiceState.Passive => new CapabilityReason(CapabilityReasonCode.PrerequisiteMissing),
            DeviceServiceState.Releasing => new CapabilityReason(CapabilityReasonCode.Quiescing),
            DeviceServiceState.Degraded or DeviceServiceState.Faulted or DeviceServiceState.ReleasedUnverified =>
                new CapabilityReason(CapabilityReasonCode.TransportFaulted),
            _ => new CapabilityReason(CapabilityReasonCode.ResourceReleased)
        };
    }

    /// <summary>
    ///     Projects a service's state onto the source-ownership choices <c>device</c>, <c>plugin</c> and
    ///     <c>unavailable</c>.
    /// </summary>
    /// <param name="state">The service's state, or null when there is no service.</param>
    /// <returns>One of the three choices.</returns>
    /// <remarks>
    ///     Acquiring and Releasing report the ownership they are moving away from rather than a transient
    ///     value: the row is read continuously, and a fourth value flickering through on every transition
    ///     reads as a fault rather than as progress.
    /// </remarks>
    public static string Ownership(DeviceServiceState? state)
    {
        return state switch
        {
            DeviceServiceState.Owned or DeviceServiceState.Releasing => SourceOwnership.Plugin,
            DeviceServiceState.Idle or DeviceServiceState.Passive or DeviceServiceState.Acquiring =>
                SourceOwnership.Device,
            _ => SourceOwnership.Unavailable
        };
    }

    private static async ValueTask TryRetractAsync(string publication, Func<ValueTask> retract)
    {
        try
        {
            await retract().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DeviceTrace.Failure("lifecycle", $"startup rollback could not retract the {publication}", ex);
        }
    }

    private static async ValueTask<DeviceServiceResult> InvokeAsync(
        DeviceServiceStatus service,
        Func<ValueTask<DeviceServiceResult>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The message says which check failed; without it a fault reads only as an exception type.
            return new DeviceServiceResult(DeviceServiceState.Faulted, new CapabilityReason(
                CapabilityReasonCode.TransportFaulted,
                DiagnosticText.FromException($"Service '{service.ServiceId}' failed", ex)));
        }
    }
}
