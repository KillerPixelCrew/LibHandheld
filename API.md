# LibHandheld API

`LibHandheld.HandheldDevice` provides direct native hardware access using types in
`LibHandheld.Contracts`. It has no dependency on a WSGM SDK, plugin host, manifest or UI layer. The
project targets .NET 10 Windows 19041, x64.

## Identification and creation

`HandheldDevice.Definitions` is the immutable list of implemented model profiles and clearly named
generic ranges. `Detect(DeviceIdentitySnapshot)` matches only supplied normalized identity and
returns a definition or null. Detection and construction acquire no hardware.
`Create(definition, stateDirectory, diagnostics)` validates the supported definition and creates its
family engine. The caller chooses a family-owned recovery directory; WSGM retains its existing
`DeviceState/<legacy family id>` directory.

A `HandheldDefinition` contains stable model ID, family ID, plain name and glyph-profile ID.
`HasController` distinguishes physical-controller implementations from CPU-only fallbacks.
`HardwareAccess` declares installation dependencies. Unimplemented inventory models do not become
supported through a detection row.

`ReadCurrentIdentity()` and `DetectCurrent()` read Windows identity without mutable transports.
`CreateCurrent(stateDirectory)` selects the current supported model.
`Create(modelId, stateDirectory)` selects a registered model explicitly. Startup revalidates the
selected model against fresh identity before hardware acquisition.

```csharp
await using var device = HandheldDevice.CreateCurrent(stateDirectory);
await device.StartAsync(controllerManagementEnabled: false);
var result = await device.SetTdpAsync(20);
// Inspect result.Applied, result.Reason and the ordered native results.
await device.StopAsync(DeviceStopReason.Exiting, Deadline.After(TimeSpan.FromSeconds(5)));
```

The same calls apply after explicit model selection. No consuming application needs to select EC
registers, firmware packets, WMI methods or CPU mailboxes.

## Native lifetime

Lifecycle transitions are serialized. A consumer can supply monotonically fresh cycle generations,
or use `StartAsync(controllerManagementEnabled)` and `ResumeAsync(deadline)` with library-owned
generations. Do not mix these two styles on one instance. Start is admitted once per instance.
Suspend and resume preserve that native owner; resume requires a successful suspension. After
terminal stop, create a new instance for another lifetime. Repeated stop returns its original result
rather than repeating native restoration. Cancellation requests cooperation; await actual native
completion before surrendering ownership.

| Operation                                                                | Behavior                                                                                                                             |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------ |
| `StartAsync(generation, controllerManagementEnabled, cancellationToken)` | Acquires services and returns Passive, Active or Degraded with an optional reason. Controller acquisition is independently optional. |
| `SuspendAsync(deadline, cancellationToken)`                              | Quiesces services without terminating the cycle.                                                                                     |
| `ResumeAsync(generation, deadline, cancellationToken)`                   | Revalidates and reacquires services for a fresh cycle.                                                                               |
| `SetControllerManagementAsync(enabled, deadline, cancellationToken)`     | Changes physical controller ownership while other services remain resident.                                                          |
| `ReleaseControllerAsync(scope, deadline, cancellationToken)`             | Releases physical acquisition and writes the documented native baseline.                                                             |
| `StopAsync(reason, deadline, cancellationToken)`                         | Restores and releases native resources; returns Clean, Unverified or Failed with an optional reason.                                 |
| `DisposeAsync()`                                                         | Joins outstanding calls, restores owned temporary state and disposes once. Call Stop first to receive restoration evidence.          |

A deadline bounds the native operation's available budget. Cancellation or timeout does not prove
that a dispatched hardware write stopped, nor that restoration succeeded. An uncertain write must
not be retried automatically.

## Controls and publications

Use `SetTdpAsync`, `SetPowerLimitsAsync`, `SetScenarioModeAsync`, `SetFanModeAsync`,
`SetFanDutyAsync`, `SetFanTargetRpmAsync`, `SetFanCurveAsync`, `SetChargeLimitAsync`,
`SetChargeProtectionAsync`, `SetChargeProtectionModeAsync`, `SetChargeBypassAsync`,
`SetChargeBypassModeAsync` and the lighting setters for ordinary controls. `Controls` gives the
current bounds, choices, instances and observations. Unsupported operations return a rejection
before writing; partial or uncertain native results remain visible and are never automatically
retried. `SetControlAsync(role, value, instanceId)` covers additional semantic roles.

A firmware charge ceiling and a software policy that toggles bypass at a percentage are different
mechanisms. Models with only native bypass expose bypass; they do not claim a firmware ceiling.

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
