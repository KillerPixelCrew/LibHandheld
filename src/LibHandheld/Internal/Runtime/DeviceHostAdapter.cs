using LibHandheld.Contracts;

namespace LibHandheld.Internal.Runtime;

/// <summary>
///     Semantic publication surface supplied by the owning handheld facade.
/// </summary>
/// <remarks>
///     Native services retain their transports and publish semantic observations to their owner.
///     Completion acknowledges dispatch to the host boundary, not downstream UI acceptance or delivery
///     to a virtual controller. A host may discard stale, invalid or obsolete publications. Published
///     collections and their elements must remain unchanged for the lifetime of the publication.
/// </remarks>
internal interface IDeviceSink
{
    /// <summary>Publishes an immutable replacement descriptor set.</summary>
    /// <param name="descriptors">Complete descriptor set.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after host-boundary dispatch; downstream acceptance is not acknowledged.</returns>
    ValueTask PublishDescriptorsAsync(
        CapabilityDescriptorSet descriptors,
        CancellationToken cancellationToken);

    /// <summary>Publishes one capability observation.</summary>
    /// <param name="state">Live semantic state.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after host-boundary dispatch; downstream acceptance is not acknowledged.</returns>
    ValueTask PublishCapabilityStateAsync(
        CapabilityState state,
        CancellationToken cancellationToken);

    /// <summary>Publishes exact physical identities the consumer may use for controller ownership.</summary>
    /// <param name="devices">Complete replacement identity set; empty retracts all physical interfaces.</param>
    /// <param name="output">What the controller can do with haptic output, or null for none.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after host-boundary dispatch; downstream acceptance is not acknowledged.</returns>
    ValueTask PublishPhysicalDevicesAsync(
        IReadOnlyList<PhysicalDeviceIdentity> devices,
        HapticCapabilities? output,
        CancellationToken cancellationToken);

    /// <summary>Publishes one complete canonical controller sample.</summary>
    /// <param name="sample">Normalized physical state.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after sample dispatch, without waiting for virtual-controller delivery.</returns>
    ValueTask PublishControllerSampleAsync(
        CanonicalControllerSample sample,
        CancellationToken cancellationToken);

    /// <summary>Publishes the closed set of assignable OEM controls.</summary>
    /// <param name="controls">Complete replacement set of logical controls; empty retracts all controls.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after host-boundary dispatch; downstream acceptance is not acknowledged.</returns>
    ValueTask PublishOemControlsAsync(
        IReadOnlyList<OemControlDescriptor> controls,
        CancellationToken cancellationToken);

    /// <summary>Publishes one deduplicated OEM-control event.</summary>
    /// <param name="controlEvent">Logical event.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after host-boundary dispatch; downstream acceptance is not acknowledged.</returns>
    ValueTask PublishOemEventAsync(
        OemControlEvent controlEvent,
        CancellationToken cancellationToken);

    /// <summary>Reports a background service failure that invalidates the active device cycle.</summary>
    /// <param name="scope">Subsystem that faulted.</param>
    /// <param name="message">Bounded diagnostic detail.</param>
    /// <remarks>
    ///     Use this only when work started by a native provider fails after its initiating lifecycle call has
    ///     already returned. Synchronous lifecycle and command failures continue to travel through
    ///     their normal result or exception path.
    /// </remarks>
    void ReportFault(string scope, string message)
    {
        DeviceTrace.Write(DeviceTraceLevel.Error, scope, message);
    }
}
