namespace LibHandheld.Internal.Lifecycle;

/// <summary>
///     Whether the device cycle continues after a controller handoff.
/// </summary>
internal enum HandoffScope
{
    /// <summary>
    ///     Only WSGM controller management was turned off.
    /// </summary>
    /// <remarks>
    ///     The host, every non-controller resource, the OEM event path, and the firmware-chord suppressor
    ///     all continue. Turning off controller emulation is not a reason to stop managing fans.
    /// </remarks>
    ControllerOnly,

    /// <summary>
    ///     The whole device cycle is ending, because WSGM is exiting or Device Integration was turned off.
    /// </summary>
    FullDeactivation
}
