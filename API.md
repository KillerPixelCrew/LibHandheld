# LibHandheld API

`LibHandheld.HandheldDevice` provides direct native hardware access using types in
`LibHandheld.Contracts`. It has no dependency on a WSGM SDK, plugin host, manifest or UI layer. The
project targets .NET 10 Windows 19041, x64.

## Identification and creation

`HandheldDevice.Definitions` is the immutable list of currently implemented exact models: five Claw
and four Ally definitions. `Detect(DeviceIdentitySnapshot)` matches only supplied normalized
identity and returns a definition or null. Detection and construction acquire no hardware.
`Create(definition, stateDirectory, diagnostics)` validates the supported definition and creates its
family engine. The caller chooses a family-owned recovery directory; WSGM retains its existing
`DeviceState/<legacy family id>` directory.

A `HandheldDefinition` contains stable model ID, family ID, plain name and glyph-profile ID.
Unimplemented inventory models do not appear as generic supported fallbacks.

## Native lifetime

The consumer serializes lifecycle transitions and supplies monotonically fresh cycle generations.
Cancellation requests cooperation; await actual native completion before surrendering ownership.

| Operation                                                                | Behavior                                                                                                                             |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------ |
| `StartAsync(generation, controllerManagementEnabled, cancellationToken)` | Acquires services and returns Passive, Active or Degraded with an optional reason. Controller acquisition is independently optional. |
| `SuspendAsync(deadline, cancellationToken)`                              | Quiesces services without terminating the cycle.                                                                                     |
| `ResumeAsync(generation, deadline, cancellationToken)`                   | Revalidates and reacquires services for a fresh cycle.                                                                               |
| `SetControllerManagementAsync(enabled, deadline, cancellationToken)`     | Changes physical controller ownership while other services remain resident.                                                          |
| `ReleaseControllerAsync(scope, deadline, cancellationToken)`             | Releases physical acquisition and writes the documented native baseline.                                                             |
| `StopAsync(reason, deadline, cancellationToken)`                         | Restores and releases native resources; returns Clean, Unverified or Failed with an optional reason.                                 |
| `DisposeAsync()`                                                         | Joins outstanding native calls and disposes once. It does not replace Stop or write restoration state itself.                        |

A deadline bounds the native operation's available budget. Cancellation or timeout does not prove
that a dispatched hardware write stopped, nor that restoration succeeded. An uncertain write must
not be retried automatically.

## Controls and publications

`ExecuteAsync(CapabilityCommand, cancellationToken)` sends a typed hardware command and preserves
its native result and timestamp. Descriptors specify capability identity, bounds, choices and
mechanics; application placement, profiles and policy belong to the consumer.
`ApplyHapticsAsync(HapticOutputFrame, cancellationToken)` sends complete motor state; explicit zero
stops output. `GetDiagnosticsAsync(cancellationToken)` returns detached, bounded native facts.
`GlyphResources` lists embedded authored glyph and notice paths; `OpenGlyphResource(relativePath)`
returns a stream for the selected resource. Original resource bytes are preserved.

`Observer` is a borrowed `IHandheldObserver`, never disposed by the device. Its typed callbacks
publish descriptor replacements, control state, physical interfaces/haptic capabilities, canonical
controller and motion samples, OEM controls/events and background faults. Collections are detached
replacements. Controller samples are complete value types; consumers must avoid per-sample logging
and allocation. Callback completion acknowledges dispatch, not downstream delivery.

The consumer owns profiles, AutoTDP decisions, HidHide, virtual controllers, OEM action routing and
UI. It must reject obsolete cycle publications and keep controller neutralization/release ahead of
terminal native stop. XML documentation beside public declarations is the detailed contract.
