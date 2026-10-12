# LibHandheld API

`LibHandheld.HandheldDevice` owns one exact handheld's native services. Its public records live in
`LibHandheld.Contracts`. The project targets .NET 10 Windows 19041, x64.

## Identification

`HandheldDevice.Definitions` lists exact supported models. `Detect(DeviceIdentitySnapshot)` matches
only the caller's identity snapshot and returns a definition or null. An unknown machine remains
passive. Detection and construction acquire no hardware.

`Create(definition, identity, stateDirectory, diagnostics)` checks that the definition matches the
supplied identity and constructs its native services. The caller chooses the state directory and
optional `Action<DeviceLogEntry>` logger. Identity is supplied once and retained for the lifetime;
families may read changing AC power or native firmware observations without repeating machine
selection.

`HandheldDefinition` contains `Id`, `FamilyId`, `Name`, `GlyphProfileId`, `HasController`,
`HardwareAccess`, `HardwareVerified` and a read-only `DeclaredRoles` snapshot. Family IDs are plain
identities such as `msi-claw`, `rog-ally`, `legion-go`, `gpd`, `ayaneo`, `onexplayer`, `valve` and
`zotac`. Exact remaining manufacturers keep their own family IDs. There are no CPU-only fallback
profiles or legacy identity migrations.

```csharp
var definition = HandheldDevice.Detect(identity);
if (definition is null) { return; }
await using var device = HandheldDevice.Create(definition, identity, stateDirectory);
await device.StartAsync(controllerManagementEnabled: false);
var result = await device.SetTdpAsync(20);
await device.StopAsync(Deadline.After(TimeSpan.FromSeconds(5)));
```

## Native lifetime

One `HandheldDevice` owner serializes commands and lifecycle transitions. An active flag controls
publication and dispatch admission; there are no lifecycle or descriptor generations. Start is
admitted once per instance. Resume requires suspension and keeps the original identity and owner.
Repeated stop returns its original result. Create a new instance after terminal stop.

| Operation                                                                           | Behavior                                                                       |
| ----------------------------------------------------------------------------------- | ------------------------------------------------------------------------------ |
| `StartAsync(bool controllerManagementEnabled = true, CancellationToken = default)`  | Acquires services; returns Passive, Active or Degraded.                        |
| `SuspendAsync(Deadline, CancellationToken = default)`                               | Quiesces native services.                                                      |
| `ResumeAsync(Deadline, CancellationToken = default)`                                | Reacquires the same model's native services.                                   |
| `SetControllerManagementAsync(bool enabled, Deadline, CancellationToken = default)` | Changes physical controller ownership while other services continue.           |
| `ReleaseControllerAsync(Deadline, CancellationToken = default)`                     | Releases physical acquisition and restores its native baseline.                |
| `StopAsync(Deadline, CancellationToken = default)`                                  | Restores originals and releases services; returns Clean, Unverified or Failed. |
| `DisposeAsync()`                                                                    | Releases handles. Call Stop first for hardware restoration and its result.     |

Cancellation requests cooperation. A timeout or interrupted write does not prove that hardware was
unchanged and never authorizes an automatic retry.

## Controls and observations

Use the semantic operations for power, scenario, fan mode/duty/RPM/native curve, charge limits,
charge protection, bypass and lighting. `SetControlAsync(role, value, instanceId, deadline, token)`
covers additional semantic roles. The consumer runs software fan curves; the library declares native
`FanCurve` only where firmware implements a curve.

`Controls` exposes current bounds, options, instances and observations. Unsupported operations are
rejected before writing. `ExecuteAsync(CapabilityCommand, CancellationToken)` dispatches a typed
vendor control. Commands contain `CommandId`, `CapabilityId`, `InstanceId`, `RequestedValue`,
`PairedPowerLimitWatts` and `Deadline`. Results contain `CommandId`, `Outcome`, `Reason` and
`CompletedAt`. A successful native write is `Applied`; readback and rollback are not result fields.
Control state comes from accepted writes and remains authoritative for the owning cycle. Controls
are available from declared write support and owned transports even before a value is known; the
library does not poll or verify their settings. Temperature and fan RPM remain sampled telemetry.
Original settings are captured once when needed for restoration or byte-preserving protocol writes;
those captures do not supply UI state or gate unrelated writes. Control timestamps retain the
accepted write time across unrelated sensor publications. Ownership timestamps change with actual
service transitions. Motion rejects obsolete or out-of-order reports before calibration and
preserves valid delayed measurements without replaying expired angle history.

Descriptors carry `CapabilityDisplay` through `Display`; its fields are `Key` and `CustomText`.
`CapabilityChoice` has `Value` and `Display`. The consumer owns page placement, localization,
profiles, desired state, AutoTDP, virtual-controller routing, HidHide and OEM actions.

`Observer` is a borrowed `IHandheldObserver`, never disposed by the device. Callbacks publish
complete descriptor replacements, control observations, physical interfaces/haptic capabilities,
full controller/motion samples, OEM controls/events and faults. Inactive owners ignore new live
publications. `ApplyHapticsAsync` sends complete motor state; a silent frame stops output.
`OemControlDescriptor.ControllerButton` identifies the Guide or Quick Access bit an OEM control
also forwards through the controller channel. It is physical metadata; the host owns alternate
dispatch when its controller consumer cannot read that channel.
`GetDiagnosticsAsync` returns `IReadOnlyDictionary<string, string>`. `GlyphResources` and
`OpenGlyphResource(relativePath)` expose embedded authored resources.

All exact definitions have authored glyph selection, including directional, touch and OEM controls.
Native OEM identifiers accompany their physical glyph mappings. Both consumers use the same original
artwork; the Avalonia adapter parses compact SVG paths with its own rendering backend.

Default AMD transports expose independent fast PPT alongside the existing sustained/slow pair.
`SetTdpAsync` updates every declared limit; explicit paired changes retain independently authored
fast values. Presets and saved custom profiles carry the optional fast target. Processor GPU clocks,
supported curve/thermal controls and Intel undervolting are ordinary typed descriptor commands, with
exact model bounds and command availability.

`CurveInputs` supplies legal starting breakpoints for an editor when a native curve is unknown; it
is neither an observation nor a write. Explicit native measurements stay separate from accepted
control targets. Valve's charge measurement is requested by an action rather than periodically
polling settings; live GPU/fabric clocks join telemetry. GPD WIN 5 publishes a read-only native
Type-C-only power-source observation from its EC; read failure returns unknown and never gates
writable controls.

`ApplyButtonHapticsAsync` and optional native pulses express supported actuator/style feedback.
Valve repeats its native motor envelope at 10 ms. Output uncertainty disables only haptics until an
explicit lifecycle or controller-management action; healthy input remains available.

## Recovery

One shared `recovery.json` stores service IDs and captured originals for the device. It preserves
the first known original until restoration or retirement. Load, capture and save failures are logged
and leave hardware writes available. Entries never lock controls or impose fixed caps. Old family
files are ignored. Hardware and restore evidence must be reported separately from compilation.
