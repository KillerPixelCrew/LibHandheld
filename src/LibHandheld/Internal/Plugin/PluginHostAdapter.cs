using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Settings;

namespace LibHandheld.Internal.Plugin;

/// <summary>
///     Semantic publication surface WSGM gives to exactly one active plugin.
/// </summary>
/// <remarks>
///     No method carries a raw transport, arbitrary operation, path, script, or executable. The plugin
///     owns its implementation and publishes only the device-independent facts WSGM consumes.
///     Completion acknowledges dispatch to the host boundary, not downstream UI acceptance or delivery
///     to a virtual controller. A host may discard stale, invalid or obsolete publications. Published
///     collections and their elements must remain unchanged for the lifetime of the publication.
/// </remarks>
internal interface IDeviceSink
{
    /// <summary>Current process/reconnect cycle generation.</summary>
    long CycleGeneration { get; }

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

    /// <summary>Publishes exact physical identities WSGM may use for its HidHide transaction.</summary>
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

    /// <summary>Declares the settings WSGM should draw, validate, store, and localize.</summary>
    /// <param name="manifest">Typed elements and the sections they belong to.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>A task completing after host-boundary dispatch; downstream acceptance is not acknowledged.</returns>
    /// <remarks>
    ///     A declaration, never UI. WSGM refuses a manifest that does not validate and keeps the
    ///     previous one, so a plugin cannot half-draw a page by publishing a broken replacement.
    ///     <para>
    ///         Settings are preferences WSGM stores and hands back; anything that writes hardware when the
    ///         user moves it is a capability and belongs in the descriptor set instead.
    ///     </para>
    /// </remarks>
    ValueTask PublishSettingsManifestAsync(
        PluginSettingsManifest manifest,
        CancellationToken cancellationToken);

    /// <summary>Writes one diagnostic line into WSGM's log.</summary>
    /// <param name="level">How much the line matters.</param>
    /// <param name="scope">Subsystem producing it, used as the log prefix.</param>
    /// <param name="message">The line, recorded whole.</param>
    /// <remarks>
    ///     Implementations must not throw. Delivery is best effort and unordered with respect to
    ///     publications. Do not base control decisions on delivery or trace high-rate input samples.
    /// </remarks>
    void Trace(DeviceTraceLevel level, string scope, string message);

    /// <summary>Records a polled state, writing only when that key's value changed.</summary>
    /// <param name="level">Level for the line when it is written.</param>
    /// <param name="scope">Subsystem producing the line.</param>
    /// <param name="key">Stable identity of the thing observed, unique within <paramref name="scope" />.</param>
    /// <param name="message">The current state.</param>
    /// <remarks>
    ///     Hosts should suppress an unchanged repeat and count it, so the next line that does change can
    ///     report how long the previous state held.
    /// </remarks>
    void TraceChange(DeviceTraceLevel level, string scope, string key, string message);

    /// <summary>Reports a background service failure that invalidates the active device cycle.</summary>
    /// <param name="scope">Subsystem that faulted.</param>
    /// <param name="message">Bounded diagnostic detail.</param>
    /// <remarks>
    ///     Use this only when work started by the plugin fails after its initiating lifecycle call has
    ///     already returned. Synchronous lifecycle and command failures continue to travel through
    ///     their normal result or exception path.
    /// </remarks>
    void ReportFault(string scope, string message)
    {
        Trace(DeviceTraceLevel.Error, scope, message);
    }
}
