# Complete API reference

Public types, declared members and enum values from the library's compiled metadata and XML
documentation. Native internals and compiler-generated record helpers are omitted. See
[integration guides](README.md) for usage and ownership.

- [ButtonHapticStrength](#buttonhapticstrength)
- [CanonicalButtons](#canonicalbuttons)
- [CanonicalControllerSample](#canonicalcontrollersample)
- [CapabilityApplyTiming](#capabilityapplytiming)
- [CapabilityChoice](#capabilitychoice)
- [CapabilityCommand](#capabilitycommand)
- [CapabilityCommandResult](#capabilitycommandresult)
- [CapabilityDescriptor](#capabilitydescriptor)
- [CapabilityDescriptorSet](#capabilitydescriptorset)
- [CapabilityDisplay](#capabilitydisplay)
- [CapabilityPersistence](#capabilitypersistence)
- [CapabilityProfileScope](#capabilityprofilescope)
- [CapabilityReason](#capabilityreason)
- [CapabilityReasonCode](#capabilityreasoncode)
- [CapabilityRole](#capabilityrole)
- [CapabilityState](#capabilitystate)
- [CapabilityUnit](#capabilityunit)
- [CapabilityValue](#capabilityvalue)
- [CapabilityValueKind](#capabilityvaluekind)
- [CommandOutcome](#commandoutcome)
- [CurvePoint](#curvepoint)
- [Deadline](#deadline)
- [DeviceControl](#devicecontrol)
- [DeviceControlResult](#devicecontrolresult)
- [DeviceControlSnapshot](#devicecontrolsnapshot)
- [DeviceHardwareAccess](#devicehardwareaccess)
- [DeviceIdentitySnapshot](#deviceidentitysnapshot)
- [DeviceLogEntry](#devicelogentry)
- [DeviceLogLevel](#deviceloglevel)
- [DeviceOperationalState](#deviceoperationalstate)
- [DeviceOperationResult](#deviceoperationresult)
- [DevicePowerMode](#devicepowermode)
- [DevicePowerPreset](#devicepowerpreset)
- [DeviceStartResult](#devicestartresult)
- [DeviceStopResult](#devicestopresult)
- [DeviceStopStatus](#devicestopstatus)
- [DisplayKey](#displaykey)
- [HandheldDefinition](#handhelddefinition)
- [HandheldResources](#handheldresources)
- [HapticActuatorSide](#hapticactuatorside)
- [HapticCapabilities](#hapticcapabilities)
- [HapticOutputFrame](#hapticoutputframe)
- [HapticPulseStyle](#hapticpulsestyle)
- [HardwareStateQuality](#hardwarestatequality)
- [IHandheldObserver](#ihandheldobserver)
- [MotionSample](#motionsample)
- [NativeHapticPulse](#nativehapticpulse)
- [OemControlDefaultActionHint](#oemcontroldefaultactionhint)
- [OemControlDescriptor](#oemcontroldescriptor)
- [OemControlEdge](#oemcontroledge)
- [OemControlEvent](#oemcontrolevent)
- [OemControlPlacement](#oemcontrolplacement)
- [OemPressKind](#oempresskind)
- [OutputChannelSupport](#outputchannelsupport)
- [PhysicalDeviceIdentity](#physicaldeviceidentity)
- [PowerLimits](#powerlimits)
- [UsbEndpointObservation](#usbendpointobservation)
- [HandheldDevice](#handhelddevice)

## ButtonHapticStrength

Namespace: `LibHandheld.Contracts`.

Strength of a native button press or release feedback event.

| Value    | Numeric value | Meaning          |
| -------- | ------------- | ---------------- |
| `Low`    | `0`           | Weak feedback.   |
| `Medium` | `1`           | Medium feedback. |
| `High`   | `2`           | Strong feedback. |

## CanonicalButtons

Namespace: `LibHandheld.Contracts`.

Every button a handheld may physically have. The canonical state represents the richest supported
handheld without assuming a virtual target. A target consumes only what it genuinely supports: the
Steam Deck composite takes rear paddles and native motion, Xbox 360 takes neither, and nothing is
synthesized to fill the gap. Gyro is passed through where the target supports motion and simply
absent where it does not â€” it is never converted into stick or mouse movement, which is the line
between calibration and remapping.

| Value             | Numeric value | Meaning                          |
| ----------------- | ------------- | -------------------------------- |
| `None`            | `0`           | Nothing pressed.                 |
| `A`               | `1`           | South face button.               |
| `B`               | `2`           | East face button.                |
| `X`               | `4`           | West face button.                |
| `Y`               | `8`           | North face button.               |
| `LeftShoulder`    | `16`          | Left shoulder.                   |
| `RightShoulder`   | `32`          | Right shoulder.                  |
| `LeftStick`       | `64`          | Left stick click.                |
| `RightStick`      | `128`         | Right stick click.               |
| `View`            | `256`         | View or Back.                    |
| `Menu`            | `512`         | Menu or Start.                   |
| `Guide`           | `1024`        | Guide or Home.                   |
| `DPadUp`          | `2048`        | D-pad up.                        |
| `DPadDown`        | `4096`        | D-pad down.                      |
| `DPadLeft`        | `8192`        | D-pad left.                      |
| `DPadRight`       | `16384`       | D-pad right.                     |
| `RearPaddle1`     | `32768`       | First rear paddle.               |
| `RearPaddle2`     | `65536`       | Second rear paddle.              |
| `RearPaddle3`     | `131072`      | Third rear paddle.               |
| `RearPaddle4`     | `262144`      | Fourth rear paddle.              |
| `LeftStickTouch`  | `524288`      | Left stick capacitive touch.     |
| `RightStickTouch` | `1048576`     | Right stick capacitive touch.    |
| `LeftPadTouch`    | `2097152`     | Left trackpad capacitive touch.  |
| `RightPadTouch`   | `4194304`     | Right trackpad capacitive touch. |
| `LeftPadClick`    | `8388608`     | Left trackpad click.             |
| `RightPadClick`   | `16777216`    | Right trackpad click.            |
| `QuickAccess`     | `33554432`    | Dedicated quick-access button.   |

## CanonicalControllerSample

Namespace: `LibHandheld.Contracts`.

One complete sample of the physical controller, normalized by its native backend. Full state rather
than deltas: a dropped delta leaves a control stuck forever, while a dropped full state is corrected
by the next one. Axes are normalized so no consumer needs to know the device's raw ranges, centres
or inversions. The native decoder owns that translation. The canonical surface covers the controls
represented by common virtual targets. Missing physical controls remain unset; consumers need no
vendor report layout. A backend reports only what its hardware has and leaves the rest alone; a
target renders only what it can represent, dropping the rest rather than remapping it. Neither side
invents a control.

| Member                                                        | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| ------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DateTimeOffset Timestamp { get; init; }`                     | High-resolution timestamp of the sample, in UTC.                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `CanonicalButtons Buttons { get; init; }`                     | Buttons currently held.                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `Single LeftStickX { get; init; }`                            | Left stick X, from -1 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `Single LeftStickY { get; init; }`                            | Left stick Y, from -1 to 1, positive up.                                                                                                                                                                                                                                                                                                                                                                                                                               |
| `Single RightStickX { get; init; }`                           | Right stick X, from -1 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `Single RightStickY { get; init; }`                           | Right stick Y, from -1 to 1, positive up.                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `Single LeftTrigger { get; init; }`                           | Left trigger, from 0 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `Single RightTrigger { get; init; }`                          | Right trigger, from 0 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `Single LeftPadX { get; init; }`                              | Left touch contact position on the horizontal axis, from -1 to 1. The touch surface is expressed as two independent contacts, left and right, because that covers both shapes WSGM's virtual targets present: the Steam Deck's two separate trackpads map one contact each, and the DualShock 4's single two-finger touchpad maps its first finger to the left contact and its second to the right. A device with neither leaves these zero and never reports a touch. |
| `Single LeftPadY { get; init; }`                              | Left touch contact position on the vertical axis, from -1 to 1, positive up.                                                                                                                                                                                                                                                                                                                                                                                           |
| `Single LeftPadForce { get; init; }`                          | Left touch contact pressure, from 0 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `Single RightPadX { get; init; }`                             | Right touch contact position on the horizontal axis, from -1 to 1.                                                                                                                                                                                                                                                                                                                                                                                                     |
| `Single RightPadY { get; init; }`                             | Right touch contact position on the vertical axis, from -1 to 1, positive up.                                                                                                                                                                                                                                                                                                                                                                                          |
| `Single RightPadForce { get; init; }`                         | Right touch contact pressure, from 0 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `Single LeftStickForce { get; init; }`                        | Left stick capacitive contact strength, from 0 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                   |
| `Single RightStickForce { get; init; }`                       | Right stick capacitive contact strength, from 0 to 1.                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `MotionSample? Motion { get; init; }`                         | Motion, when the device has a sensor and it is available.                                                                                                                                                                                                                                                                                                                                                                                                              |
| `CanonicalControllerSample Neutral(DateTimeOffset timestamp)` | The neutral sample: nothing held, every axis centred. Published to the virtual target whenever forwarding stops â€” UI capture, target removal, game exit, suspend, disconnect, plugin disable, or fault. Without it the last forwarded state stays latched and the game keeps seeing a held control. `timestamp`: Timestamp to stamp on the neutral sample. A sample with every control at rest.                                                                      |

## CapabilityApplyTiming

Namespace: `LibHandheld.Contracts`.

When a written capability value takes effect.

| Value                  | Numeric value | Meaning                                                                         |
| ---------------------- | ------------- | ------------------------------------------------------------------------------- |
| `Immediate`            | `0`           | At once, including inside a running game.                                       |
| `NextApplicationStart` | `1`           | The next time an application starts; a running game keeps what it started with. |
| `SystemRestart`        | `2`           | After Windows restarts.                                                         |

## CapabilityChoice

Namespace: `LibHandheld.Contracts`.

One legal option of a choice capability. `Value`: Stable machine value, used in commands and
persisted state. `Display`: How WSGM labels the option. It must be non-null and satisfy
LibHandheld.Contracts.CapabilityDisplay.TryValidate(System.String@) wherever the choice is accepted.

| Member                                                      | Contract                                                                                                                                                                                                                                                                                  |
| ----------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityChoice(String Value, CapabilityDisplay Display)` | One legal option of a choice capability. `Value`: Stable machine value, used in commands and persisted state. `Display`: How WSGM labels the option. It must be non-null and satisfy LibHandheld.Contracts.CapabilityDisplay.TryValidate(System.String@) wherever the choice is accepted. |
| `String Value { get; init; }`                               | Stable machine value, used in commands and persisted state.                                                                                                                                                                                                                               |
| `CapabilityDisplay Display { get; init; }`                  | How WSGM labels the option. It must be non-null and satisfy LibHandheld.Contracts.CapabilityDisplay.TryValidate(System.String@) wherever the choice is accepted.                                                                                                                          |

## CapabilityCommand

Namespace: `LibHandheld.Contracts`.

A request to change or invoke one capability. The active owner validates the value, live service
availability and deadline before writing. Rejection does not authorize the plugin to resubmit a
command or choose a replacement value.

| Member                                           | Contract                                                                                    |
| ------------------------------------------------ | ------------------------------------------------------------------------------------------- |
| `CapabilityCommand()`                            |                                                                                             |
| `Guid CommandId { get; init; }`                  | Unique identifier for this command, used to correlate the outcome.                          |
| `String CapabilityId { get; init; }`             | Capability being commanded.                                                                 |
| `String InstanceId { get; init; }`               | Instance discriminator, matching the descriptor.                                            |
| `CapabilityValue RequestedValue { get; init; }`  | The requested value, or null for an action.                                                 |
| `Int32? PairedPowerLimitWatts { get; init; }`    | The other member of a consumer-coordinated power limit pair.                                |
| `Boolean ApplyUniformPowerTarget { get; init; }` | Whether this coordinated command requests one uniform target on every declared power limit. |
| `Deadline Deadline { get; init; }`               | When the command stops being worth applying, on the active clock.                           |

## CapabilityCommandResult

Namespace: `LibHandheld.Contracts`.

The result of a capability command.

| Member                                      | Contract                                     |
| ------------------------------------------- | -------------------------------------------- |
| `CapabilityCommandResult()`                 |                                              |
| `Guid CommandId { get; init; }`             | The command this result answers.             |
| `CommandOutcome Outcome { get; init; }`     | How it finished.                             |
| `CapabilityReason Reason { get; init; }`    | Why, when the outcome was not a clean apply. |
| `DateTimeOffset CompletedAt { get; init; }` | When the command finished, in UTC.           |

## CapabilityDescriptor

Namespace: `LibHandheld.Contracts`.

Immutable native capability description; the consumer owns page layout.

| Member                                                         | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| -------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityDescriptor()`                                       |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `String CapabilityId { get; init; }`                           | Stable capability identifier, for example power.primary-limit.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `String InstanceId { get; init; }`                             | Instance discriminator when a device has several of the same capability, such as two fans.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `CapabilityRole Role { get; init; }`                           | What this capability means to WSGM.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `CapabilityValueKind ValueKind { get; init; }`                 | The shape of its value.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| `CapabilityDisplay Display { get; init; }`                     | How WSGM labels it.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `Boolean SupportsRead { get; init; }`                          | Whether the current value can be read back from hardware.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| `Boolean SupportsWrite { get; init; }`                         | Whether a new value can be written.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `Boolean SupportsAction { get; init; }`                        | Whether the capability can be invoked as a one-shot action.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| `Int32? Minimum { get; init; }`                                | Inclusive minimum for an integer capability.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `Int32? Maximum { get; init; }`                                | Inclusive maximum for an integer capability.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| `Int32? Step { get; init; }`                                   | Step between legal integer values.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| `CapabilityUnit Unit { get; init; }`                           | Unit of a numeric value.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `String PairedPowerLimitId { get; init; }`                     | Optional single-instance boost-limit capability coordinated with this sustained limit. When present, every write to either limit carries the other one in LibHandheld.Contracts.CapabilityCommand.PairedPowerLimitWatts. The host decides the pair: it keeps the sustained limit at or below the boost limit, and a unified target moves both to the same wattage within the boost descriptor's range. The plugin writes the two values as given and never derives one from the other. The host snapshots both observed limits before taking ownership and restores the sustained pair followed by the original boost value where those originals are known. Both descriptors must be writable integer watt limits with valid bounds and step; missing readback does not disable writing. |
| `IReadOnlyList<CapabilityChoice> Choices { get; init; }`       | Legal options for a choice capability.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `IReadOnlyList<Int32> CurveInputs { get; init; }`              | Source-declared starting inputs for a curve editor when no current curve can be read. These form an editable starting template, never an observed hardware value. A reported curve supplies its own inputs instead.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `IReadOnlyList<DevicePowerPreset> PowerPresets { get; init; }` | Optional device-authored shortcuts on the single-instance sustained power limit. Empty by default. Assignment takes a read-only snapshot so later edits to the supplied collection cannot change this descriptor. See LibHandheld.Contracts.DevicePowerPreset for bounds and host behavior.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                               |
| `Int32? MaximumLength { get; init; }`                          | Longest accepted value for a LibHandheld.Contracts.CapabilityValueKind.Text capability. Required for text, ignored otherwise. There is no default: a text capability that declared no bound would be the one value shape with no limit at all, which is exactly what LibHandheld.Internal.Capabilities.PlainText exists to prevent.                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `Boolean AvailableOnAc { get; init; }`                         | Whether the capability is available while running on AC power.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `Boolean AvailableOnDc { get; init; }`                         | Whether the capability is available while running on battery. AC/DC is a descriptor field, not a descriptor generation: the power source changes constantly and republishing every descriptor on each transition would invalidate caches for no reason. The live condition is reported through capability state instead.                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `CapabilityPersistence Persistence { get; init; }`             | How long a written value survives.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| `CapabilityProfileScope ProfileScope { get; init; }`           | How WSGM carries a remembered value from one game to the next.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `CapabilityApplyTiming ApplyTiming { get; init; }`             | When a written value takes effect.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |

## CapabilityDescriptorSet

Namespace: `LibHandheld.Contracts`.

A complete replacement set of descriptors for the active device. Descriptors are always published as
a whole set. A capability missing from a new set has gone away and its control disappears, rather
than lingering as permanently unavailable.

| Member                                                           | Contract                                      |
| ---------------------------------------------------------------- | --------------------------------------------- |
| `CapabilityDescriptorSet()`                                      |                                               |
| `IReadOnlyList<CapabilityDescriptor> Descriptors { get; init; }` | Every capability the device currently offers. |

## CapabilityDisplay

Namespace: `LibHandheld.Contracts`.

A semantic label key or native control's custom label. The consumer owns presentation and
localization. Native providers select a semantic key; controls without a shared key supply ordinary
text through CustomText. LibHandheld.Contracts.CapabilityDisplay.CustomText describes a
device-specific control that no key covers, such as a vendor toggle. It is ordinary text, never a
format string, markup, or localization key.

| Member                               | Contract                                                                                                                                                                                 |
| ------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityDisplay()`                |                                                                                                                                                                                          |
| `DisplayKey Key { get; init; }`      | The semantic display key, or LibHandheld.Contracts.DisplayKey.Custom. Undefined numeric enum values are rejected by LibHandheld.Contracts.CapabilityDisplay.TryValidate(System.String@). |
| `String CustomText { get; init; }`   | Native control label, used only when LibHandheld.Contracts.CapabilityDisplay.Key is LibHandheld.Contracts.DisplayKey.Custom. The consumer can localize shared keys.                      |
| `Boolean TryValidate(String& error)` | Whether this display metadata is usable. `error`: The reason it is not, when the result is false. true when the label is safe to render.                                                 |

## CapabilityPersistence

Namespace: `LibHandheld.Contracts`.

How long a written capability value survives.

| Value              | Numeric value | Meaning                                                             |
| ------------------ | ------------- | ------------------------------------------------------------------- |
| `Unknown`          | `0`           | Not established. Treated as device-persistent by every safety rule. |
| `Volatile`         | `1`           | Lost when the device loses power.                                   |
| `DevicePersistent` | `2`           | Stored on the device and survives reboot.                           |

## CapabilityProfileScope

Namespace: `LibHandheld.Contracts`.

How WSGM carries a remembered capability value from one game to the next.

| Value                  | Numeric value | Meaning                                                                                                                                                                                                                                                         |
| ---------------------- | ------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Switched`             | `0`           | WSGM resolves the running game's value and writes it when the game changes. Every value published before version 11 behaves this way.                                                                                                                           |
| `GlobalOnly`           | `1`           | One machine-wide value. A change is always saved to Global and never offered per game, typically because the value holds only after a restart.                                                                                                                  |
| `NativePerApplication` | `2`           | The publisher's driver keeps per-application values itself. WSGM writes the Global value through commands and hands every game's overrides, with its executables, to the publisher, which stores them natively so the driver applies them when the game starts. |

## CapabilityReason

Namespace: `LibHandheld.Contracts`.

A structured reason, carrying separate user-facing and diagnostic detail. The split is deliberate.
LibHandheld.Contracts.CapabilityReason.Detail may name a provider, a conflicting process, or a
firmware version — useful in a log, wrong in an overlay tile. WSGM renders the code through its own
localized strings and shows the detail only in diagnostics. `Code`: The stable reason. `Detail`:
Diagnostic detail, never rendered as the primary user-facing message. `Retryable`: Whether retrying
the same request could plausibly succeed later.

| Member                                                                                                 | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| ------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityReason(CapabilityReasonCode Code, String Detail = null/default, Boolean Retryable = False)` | A structured reason, carrying separate user-facing and diagnostic detail. The split is deliberate. LibHandheld.Contracts.CapabilityReason.Detail may name a provider, a conflicting process, or a firmware version — useful in a log, wrong in an overlay tile. WSGM renders the code through its own localized strings and shows the detail only in diagnostics. `Code`: The stable reason. `Detail`: Diagnostic detail, never rendered as the primary user-facing message. `Retryable`: Whether retrying the same request could plausibly succeed later. |
| `CapabilityReasonCode Code { get; init; }`                                                             | The stable reason.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| `String Detail { get; init; }`                                                                         | Diagnostic detail, never rendered as the primary user-facing message.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| `Boolean Retryable { get; init; }`                                                                     | Whether retrying the same request could plausibly succeed later.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |

## CapabilityReasonCode

Namespace: `LibHandheld.Contracts`.

Why a capability is unavailable, degraded, or refused a command. A closed taxonomy rather than a
message string, because the UI has to decide what to _do_: offer a retry, point at a missing
prerequisite, name the conflicting owner, or say nothing can be done. A free-text reason would force
that decision back onto string matching.

| Value                      | Numeric value | Meaning                                                                                  |
| -------------------------- | ------------- | ---------------------------------------------------------------------------------------- |
| `Unsupported`              | `0`           | The device does not implement this capability.                                           |
| `PrerequisiteMissing`      | `1`           | A required provider, driver, library, or helper is absent.                               |
| `ResourceConflict`         | `2`           | Another owner currently holds the resource.                                              |
| `ResourceReleased`         | `3`           | The plugin released the resource, for example when controller management was turned off. |
| `UnavailableOnPowerSource` | `4`           | Not available on the current power source.                                               |
| `TransportFaulted`         | `5`           | The transport failed and the capability stays faulted until recovery.                    |
| `ObservationExpired`       | `6`           | The observation expired under the freshness policy.                                      |
| `HostUnavailable`          | `7`           | The plugin runtime is unavailable, so nothing can be observed or commanded.              |
| `FirmwareNotVerified`      | `8`           | The firmware is outside the range this implementation was verified against.              |
| `ValueOutOfRange`          | `9`           | The requested value is outside what the hardware currently accepts.                      |
| `Quiescing`                | `10`          | The device is quiescing for suspend or shutdown and takes no new work.                   |

## CapabilityRole

Namespace: `LibHandheld.Contracts`.

What a capability means, independent of how any device implements it. Consumers receive semantic
roles, units and bounds without native transport details. The backend owns vendor WMI, EC, SMU or
MMIO operations. Vendor-specific roles cover features such as UMA allocation, a secondary display or
USB-C routing.

| Value                  | Numeric value | Meaning                                                                                                                                                                                                                |
| ---------------------- | ------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PowerSustainedLimit`  | `0`           | Sustained processor power limit.                                                                                                                                                                                       |
| `PowerSlowLimit`       | `1`           | Slow-window processor power limit.                                                                                                                                                                                     |
| `PowerFastLimit`       | `2`           | Fast-window processor power limit.                                                                                                                                                                                     |
| `PowerPeakLimit`       | `3`           | Peak or burst processor power limit.                                                                                                                                                                                   |
| `ScenarioMode`         | `4`           | Vendor performance or scenario mode.                                                                                                                                                                                   |
| `FanMode`              | `5`           | Fan operating mode, such as automatic, custom, or full speed.                                                                                                                                                          |
| `FanDuty`              | `6`           | Fan duty cycle for one channel.                                                                                                                                                                                        |
| `FanTargetRpm`         | `7`           | Target fan speed in RPM for one channel.                                                                                                                                                                               |
| `FanCurve`             | `8`           | Editable fan curve for one channel.                                                                                                                                                                                    |
| `FanMeasuredRpm`       | `9`           | Measured fan speed for one channel.                                                                                                                                                                                    |
| `ChargeLimit`          | `10`          | Battery charge ceiling.                                                                                                                                                                                                |
| `ChargeProtectionMode` | `11`          | Battery charge protection mode.                                                                                                                                                                                        |
| `ChargeBypass`         | `12`          | Bypass or pass-through charging.                                                                                                                                                                                       |
| `LightingPower`        | `13`          | Master lighting power.                                                                                                                                                                                                 |
| `LightingBrightness`   | `14`          | Lighting brightness.                                                                                                                                                                                                   |
| `LightingZoneColor`    | `15`          | Colour of one lighting zone.                                                                                                                                                                                           |
| `LightingEffect`       | `16`          | Lighting effect selection.                                                                                                                                                                                             |
| `LightingEffectSpeed`  | `17`          | Lighting effect speed.                                                                                                                                                                                                 |
| `Telemetry`            | `18`          | A hardware telemetry reading such as a temperature or a power draw.                                                                                                                                                    |
| `ControllerSource`     | `19`          | The physical controller input source.                                                                                                                                                                                  |
| `MotionSource`         | `20`          | The motion sensor source.                                                                                                                                                                                              |
| `HapticSink`           | `21`          | A rumble or haptic output sink.                                                                                                                                                                                        |
| `VariableRefreshRate`  | `22`          | Variable refresh rate for a panel. A graphics driver normally owns adaptive sync, such as Intel IGCL's Arc Sync. A handheld provider may expose it for a panel only it can reach. The consumer chooses its projection. |
| `OemControl`           | `23`          | A logical OEM control the user may reassign.                                                                                                                                                                           |
| `GenericToggle`        | `24`          | A device-specific on/off switch with no more specific role.                                                                                                                                                            |
| `GenericRange`         | `25`          | A device-specific numeric range with no more specific role.                                                                                                                                                            |
| `GenericChoice`        | `26`          | A device-specific choice from a fixed set with no more specific role.                                                                                                                                                  |
| `GenericAction`        | `27`          | A device-specific one-shot action with no more specific role.                                                                                                                                                          |
| `GenericText`          | `28`          | A device-specific short text value with no more specific role.                                                                                                                                                         |
| `GenericReadOnly`      | `29`          | A device-specific read-only value with no more specific role.                                                                                                                                                          |

## CapabilityState

Namespace: `LibHandheld.Contracts`.

The live state of one capability as reported by its native owner. Versioned separately from the
descriptor because it changes constantly while the descriptor does not. It carries only what the
plugin observed; WSGM's desired value and UI progress live in WSGM's capability projection and are
never mixed in here â€” a plugin does not own what the user asked for.

| Member                                         | Contract                                                                                                                                                                                       |
| ---------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityState()`                            |                                                                                                                                                                                                |
| `String CapabilityId { get; init; }`           | Capability this state belongs to.                                                                                                                                                              |
| `String InstanceId { get; init; }`             | Instance discriminator, matching the descriptor.                                                                                                                                               |
| `Boolean Available { get; init; }`             | Whether the capability can currently be used.                                                                                                                                                  |
| `CapabilityReason Reason { get; init; }`       | Why it is unavailable or degraded. Null when it is healthy.                                                                                                                                    |
| `CapabilityValue ObservedValue { get; init; }` | The last read or successfully written value, or null when unknown or action-only. Must match the descriptor's value shape; LibHandheld.Contracts.CapabilityState.Quality records its evidence. |
| `HardwareStateQuality Quality { get; init; }`  | How much LibHandheld.Contracts.CapabilityState.ObservedValue can be trusted.                                                                                                                   |
| `DateTimeOffset? ObservedAt { get; init; }`    | When the read or accepted write was observed, in UTC; null when no observation exists.                                                                                                         |

## CapabilityUnit

Namespace: `LibHandheld.Contracts`.

Units a numeric capability may carry. A closed set rather than a free-text unit string: WSGM formats
and localizes these, and a plugin supplying "Watt " or "watts" would leak straight into the UI.

| Value         | Numeric value | Meaning                 |
| ------------- | ------------- | ----------------------- |
| `None`        | `0`           | Dimensionless.          |
| `Watt`        | `1`           | Watts.                  |
| `Percent`     | `2`           | Percent.                |
| `Celsius`     | `3`           | Degrees Celsius.        |
| `Rpm`         | `4`           | Revolutions per minute. |
| `Milliampere` | `5`           | Milliamperes.           |
| `Millivolt`   | `6`           | Millivolts.             |
| `Megahertz`   | `7`           | Megahertz.              |
| `Millisecond` | `8`           | Milliseconds.           |

## CapabilityValue

Namespace: `LibHandheld.Contracts`.

A capability value in whichever shape its descriptor declares. Exactly the field selected by
LibHandheld.Contracts.CapabilityValue.Kind carries a value; unused scalar fields stay null and
unused curves stay empty. Construction and factory methods do not validate descriptor bounds.
Validate with LibHandheld.Internal.Capabilities.CapabilityValueValidation before accepting a value.
Collection properties retain the supplied list, which must not be mutated after publication.

| Member                                                    | Contract                                                                                                                                                                                                                                                                                    |
| --------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityValue()`                                       |                                                                                                                                                                                                                                                                                             |
| `CapabilityValueKind Kind { get; init; }`                 | Which field carries the value.                                                                                                                                                                                                                                                              |
| `Boolean? BooleanValue { get; init; }`                    | Value when LibHandheld.Contracts.CapabilityValue.Kind is LibHandheld.Contracts.CapabilityValueKind.Boolean.                                                                                                                                                                                 |
| `Int32? IntegerValue { get; init; }`                      | Value when LibHandheld.Contracts.CapabilityValue.Kind is LibHandheld.Contracts.CapabilityValueKind.Integer.                                                                                                                                                                                 |
| `String ChoiceValue { get; init; }`                       | Selected option when LibHandheld.Contracts.CapabilityValue.Kind is LibHandheld.Contracts.CapabilityValueKind.Choice.                                                                                                                                                                        |
| `Int32? ColorValue { get; init; }`                        | Packed 24-bit RGB when LibHandheld.Contracts.CapabilityValue.Kind is LibHandheld.Contracts.CapabilityValueKind.Color.                                                                                                                                                                       |
| `IReadOnlyList<CurvePoint> CurveValue { get; init; }`     | Curve points when LibHandheld.Contracts.CapabilityValue.Kind is LibHandheld.Contracts.CapabilityValueKind.Curve.                                                                                                                                                                            |
| `String TextValue { get; init; }`                         | Text when LibHandheld.Contracts.CapabilityValue.Kind is LibHandheld.Contracts.CapabilityValueKind.Text. Bounded by the descriptor's LibHandheld.Contracts.CapabilityDescriptor.MaximumLength and checked against LibHandheld.Internal.Capabilities.PlainText before it reaches any surface. |
| `CapabilityValue None()`                                  | Creates a LibHandheld.Contracts.CapabilityValueKind.None value, which carries no field. The value.                                                                                                                                                                                          |
| `CapabilityValue Boolean(Boolean value)`                  | Creates a LibHandheld.Contracts.CapabilityValueKind.Boolean value. `value`: The boolean. The value with LibHandheld.Contracts.CapabilityValue.BooleanValue set.                                                                                                                             |
| `CapabilityValue Integer(Int32 value)`                    | Creates a LibHandheld.Contracts.CapabilityValueKind.Integer value. `value`: The integer. The value with LibHandheld.Contracts.CapabilityValue.IntegerValue set.                                                                                                                             |
| `CapabilityValue Choice(String value)`                    | Creates a LibHandheld.Contracts.CapabilityValueKind.Choice value. `value`: The selected option. The value with LibHandheld.Contracts.CapabilityValue.ChoiceValue set.                                                                                                                       |
| `CapabilityValue Color(Int32 value)`                      | Creates a LibHandheld.Contracts.CapabilityValueKind.Color value. `value`: Packed 24-bit RGB. The value with LibHandheld.Contracts.CapabilityValue.ColorValue set.                                                                                                                           |
| `CapabilityValue Curve(IReadOnlyList<CurvePoint> points)` | Creates a LibHandheld.Contracts.CapabilityValueKind.Curve value. `points`: The curve points. The list is stored as given, not copied. The value with LibHandheld.Contracts.CapabilityValue.CurveValue set.                                                                                  |
| `CapabilityValue Text(String value)`                      | Creates a LibHandheld.Contracts.CapabilityValueKind.Text value. `value`: The text. Validation against the descriptor stays with the caller. The value with LibHandheld.Contracts.CapabilityValue.TextValue set.                                                                             |

## CapabilityValueKind

Namespace: `LibHandheld.Contracts`.

The shape of a capability's value, which decides how it is rendered and validated.

| Value     | Numeric value | Meaning                                                                                                                                                                                                                                                              |
| --------- | ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `None`    | `0`           | No value; the capability is invoked rather than set.                                                                                                                                                                                                                 |
| `Boolean` | `1`           | A boolean.                                                                                                                                                                                                                                                           |
| `Integer` | `2`           | An integer within a declared range.                                                                                                                                                                                                                                  |
| `Choice`  | `3`           | One option from a declared set.                                                                                                                                                                                                                                      |
| `Color`   | `4`           | A colour, expressed as 24-bit RGB.                                                                                                                                                                                                                                   |
| `Curve`   | `5`           | An ordered set of points, such as a fan curve.                                                                                                                                                                                                                       |
| `Text`    | `6`           | Short plain text the user types. The only kind not constrained by construction, so it carries the same text contract as LibHandheld.Contracts.CapabilityDisplay.CustomText: a declared maximum length with control and bidirectional formatting characters rejected. |

## CommandOutcome

Namespace: `LibHandheld.Contracts`.

The native outcome of one command.

| Value           | Numeric value | Meaning                                                                                                                                                                                                          |
| --------------- | ------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Accepted`      | `0`           | Validated and kept for a later apply; nothing has reached the hardware yet. WSGM returns it for a native per-application value saved to the running game's profile, which the driver applies from the next sync. |
| `Applied`       | `1`           | The native transport accepted the requested write.                                                                                                                                                               |
| `Rejected`      | `2`           | Refused before anything was written.                                                                                                                                                                             |
| `TimedOut`      | `3`           | The deadline passed. Whether it was applied is unknown.                                                                                                                                                          |
| `Indeterminate` | `4`           | Interrupted mid-operation. Whether it was applied is unknown.                                                                                                                                                    |

## CurvePoint

Namespace: `LibHandheld.Contracts`.

One point of a capability curve, such as a fan table entry. `Input`: The independent value, for
example a temperature in Celsius. `Output`: The dependent value, for example a duty cycle in
percent.

| Member                                  | Contract                                                                                                                                                                                              |
| --------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CurvePoint(Int32 Input, Int32 Output)` | One point of a capability curve, such as a fan table entry. `Input`: The independent value, for example a temperature in Celsius. `Output`: The dependent value, for example a duty cycle in percent. |
| `Int32 Input { get; init; }`            | The independent value, for example a temperature in Celsius.                                                                                                                                          |
| `Int32 Output { get; init; }`           | The dependent value, for example a duty cycle in percent.                                                                                                                                             |

## Deadline

Namespace: `LibHandheld.Contracts`.

A point in time on LibHandheld.Internal.Lifecycle.ActiveClock: the moment by which an operation must
have finished, counting only time the process could run. Uses the process-local clamped monotonic
clock rather than a continuously running UTC clock. Long scheduling or suspension gaps consume at
most LibHandheld.Internal.Lifecycle.ActiveClock.MaximumStep. A deadline signals cooperative
cancellation; it cannot terminate a native call.

| Member                                                                         | Contract                                                                                                                                                                                                                                                                                                                                                                                     |
| ------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Deadline Expired { get; }`                                                    | A deadline that has already passed.                                                                                                                                                                                                                                                                                                                                                          |
| `Deadline Never { get; }`                                                      | A deadline that never passes.                                                                                                                                                                                                                                                                                                                                                                |
| `TimeSpan Remaining { get; }`                                                  | The time left before the deadline, never negative.                                                                                                                                                                                                                                                                                                                                           |
| `Boolean HasExpired { get; }`                                                  | Whether the deadline has passed.                                                                                                                                                                                                                                                                                                                                                             |
| `Deadline After(TimeSpan budget)`                                              | A deadline budget of active time from now. `budget`: Active-time budget; zero or negative budgets expire immediately. A deadline from the current active clock, saturated to LibHandheld.Contracts.Deadline.Never on overflow.                                                                                                                                                               |
| `Deadline At(DateTimeOffset utc)`                                              | The active-time deadline equivalent to a wall-clock one, measured from now. `utc`: Wall-clock target converted once to an active-time budget; later UTC changes do not alter it. The deadline, already expired when utc has passed.                                                                                                                                                          |
| `Deadline Earliest(Deadline other)`                                            | The earlier of two deadlines. `other`: The other deadline. Whichever comes first.                                                                                                                                                                                                                                                                                                            |
| `CancellationTokenSource CreateCancellationSource(CancellationToken[] linked)` | A source that cancels when the deadline passes or any linked token cancels. `linked`: Further tokens that cancel it. The source, which the caller disposes. It is already cancelled when the deadline has passed. A timer on the wall clock would fire the moment a frozen process thaws, so the source is cancelled by LibHandheld.Internal.Lifecycle.ActiveClock instead, within its tick. |
| `Boolean Equals(Deadline other)`                                               |                                                                                                                                                                                                                                                                                                                                                                                              |
| `Boolean Equals(Object obj)`                                                   |                                                                                                                                                                                                                                                                                                                                                                                              |
| `Int32 GetHashCode()`                                                          |                                                                                                                                                                                                                                                                                                                                                                                              |
| `Int32 CompareTo(Deadline other)`                                              |                                                                                                                                                                                                                                                                                                                                                                                              |
| `Boolean op_Equality(Deadline left, Deadline right)`                           | Whether two deadlines are the same moment. `left`: First deadline on the process-local active clock. `right`: Second deadline on the same clock. True when both store the same active-clock instant.                                                                                                                                                                                         |
| `Boolean op_Inequality(Deadline left, Deadline right)`                         | Whether two deadlines differ. `left`: First deadline on the process-local active clock. `right`: Second deadline on the same clock. True when the stored active-clock instants differ.                                                                                                                                                                                                       |
| `Boolean op_LessThan(Deadline left, Deadline right)`                           | Whether the left deadline comes first. `left`: First deadline on the process-local active clock. `right`: Second deadline on the same clock. True when the left instant precedes the right instant.                                                                                                                                                                                          |
| `Boolean op_GreaterThan(Deadline left, Deadline right)`                        | Whether the left deadline comes later. `left`: First deadline on the process-local active clock. `right`: Second deadline on the same clock. True when the left instant follows the right instant.                                                                                                                                                                                           |
| `Boolean op_LessThanOrEqual(Deadline left, Deadline right)`                    | Whether the left deadline comes first or at the same moment. `left`: First deadline on the process-local active clock. `right`: Second deadline on the same clock. True when the left instant precedes or equals the right instant.                                                                                                                                                          |
| `Boolean op_GreaterThanOrEqual(Deadline left, Deadline right)`                 | Whether the left deadline comes later or at the same moment. `left`: First deadline on the process-local active clock. `right`: Second deadline on the same clock. True when the left instant follows or equals the right instant.                                                                                                                                                           |
| `String ToString()`                                                            |                                                                                                                                                                                                                                                                                                                                                                                              |

## DeviceControl

Namespace: `LibHandheld.Contracts`.

A currently declared semantic control and its last native observation. `Descriptor`: Detached
immutable shape, options and bounds. `State`: Current-generation observation, or null when the value
has not been reported.

| Member                                                                  | Contract                                                                                                                                                                                                                  |
| ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceControl(CapabilityDescriptor Descriptor, CapabilityState State)` | A currently declared semantic control and its last native observation. `Descriptor`: Detached immutable shape, options and bounds. `State`: Current-generation observation, or null when the value has not been reported. |
| `CapabilityDescriptor Descriptor { get; init; }`                        | Detached immutable shape, options and bounds.                                                                                                                                                                             |
| `CapabilityState State { get; init; }`                                  | Current-generation observation, or null when the value has not been reported.                                                                                                                                             |
| `CapabilityRole Role { get; }`                                          | Meaning of the control.                                                                                                                                                                                                   |
| `String InstanceId { get; }`                                            | Logical instance suitable for fan and lighting selectors.                                                                                                                                                                 |
| `CapabilityValue ObservedValue { get; }`                                | The native value; this never substitutes a desired or guessed value.                                                                                                                                                      |

## DeviceControlResult

Namespace: `LibHandheld.Contracts`.

One actual native outcome from a semantic operation. `Role`: Meaning of the control, independent of
its implementation. `InstanceId`: Logical fan, zone or other instance, when there is more than one.
`Result`: Unmodified native command outcome.

| Member                                                                                        | Contract                                                                                                                                                                                                                                             |
| --------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceControlResult(CapabilityRole Role, String InstanceId, CapabilityCommandResult Result)` | One actual native outcome from a semantic operation. `Role`: Meaning of the control, independent of its implementation. `InstanceId`: Logical fan, zone or other instance, when there is more than one. `Result`: Unmodified native command outcome. |
| `CapabilityRole Role { get; init; }`                                                          | Meaning of the control, independent of its implementation.                                                                                                                                                                                           |
| `String InstanceId { get; init; }`                                                            | Logical fan, zone or other instance, when there is more than one.                                                                                                                                                                                    |
| `CapabilityCommandResult Result { get; init; }`                                               | Unmodified native command outcome.                                                                                                                                                                                                                   |

## DeviceControlSnapshot

Namespace: `LibHandheld.Contracts`.

One detached view of current device controls, taken under a single publication lock.

| Member                                                                      | Contract                                                                                                                                                                                                                                    |
| --------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceControlSnapshot()`                                                   |                                                                                                                                                                                                                                             |
| `IReadOnlyList<DeviceControl> Controls { get; init; }`                      | Declared controls and their matching current observations.                                                                                                                                                                                  |
| `PowerLimits ObservedPowerLimits { get; }`                                  | Last reliable sustained and optional boost observations, or null when sustained power is unknown.                                                                                                                                           |
| `Int32? ObservedChargeLimitPercent { get; }`                                | Last reliable battery charge ceiling, or null when unknown.                                                                                                                                                                                 |
| `Boolean? ObservedChargeProtectionEnabled { get; }`                         | Last reliable Boolean battery-protection observation, or null for choice controls or unknown state.                                                                                                                                         |
| `String ObservedChargeProtectionMode { get; }`                              | Last reliable choice battery-protection mode, or null for Boolean controls or unknown state.                                                                                                                                                |
| `String ObservedChargeBypassMode { get; }`                                  | Last reliable choice bypass-charging mode, or null for Boolean controls or unknown state.                                                                                                                                                   |
| `Boolean? ObservedChargeBypassEnabled { get; }`                             | Last reliable bypass-charging observation. Boolean controls report their exact value; known choice modes disabled, until-sleep and always report their semantics. Other modes or unknown state return null.                                 |
| `IReadOnlyList<DeviceControl> Fans { get; }`                                | All declared fan controls and measured fan readings, including their logical instance identities.                                                                                                                                           |
| `IReadOnlyList<DeviceControl> Lighting { get; }`                            | All declared lighting controls and observed states, including logical zone identities.                                                                                                                                                      |
| `DeviceControl Find(CapabilityRole role, String instanceId = null/default)` | Finds one semantic control. A logical instance must be supplied when a role is ambiguous. `role`: Meaning of the requested control. `instanceId`: Optional logical instance. The single matching control, or null when absent or ambiguous. |

## DeviceHardwareAccess

Namespace: `LibHandheld.Contracts`.

Native dependencies required by a model, independently of application controller routing.

| Value     | Numeric value | Meaning                                                               |
| --------- | ------------- | --------------------------------------------------------------------- |
| `None`    | `0`           | No privileged driver helper is required.                              |
| `PawnIo`  | `1`           | Signed PawnIO modules provide embedded-controller or CPU access.      |
| `IntelKx` | `2`           | The pinned Intel KX helper provides the supported write path.         |
| `InpOut`  | `4`           | The pinned InpOut helper provides Steam Deck EC port and MMIO access. |

## DeviceIdentitySnapshot

Namespace: `LibHandheld.Contracts`.

The normalized machine facts a device definition's identity observations are matched against. This
is the observed half of identity. The backend's exact model predicates match what the machine
actually reports. Producing it is platform work owned by Device Lab and WSGM's runtime; the contract
only fixes which facts exist and how they compare, so both sides agree on what "matched" means.
Every value arrives with normalized whitespace. Comparison is ordinal and case-insensitive on the
normalized form, so a vendor that changes casing or padding between firmware revisions does not
silently stop matching.

| Member                                                              | Contract                                                                                                                                                                                                                                                 |
| ------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceIdentitySnapshot()`                                          |                                                                                                                                                                                                                                                          |
| `String SystemManufacturer { get; init; }`                          | SMBIOS Type 1 manufacturer.                                                                                                                                                                                                                              |
| `String SystemProduct { get; init; }`                               | SMBIOS Type 1 product name. Marketing text.                                                                                                                                                                                                              |
| `String SystemSku { get; init; }`                                   | SMBIOS Type 1 SKU number.                                                                                                                                                                                                                                |
| `String SystemFamily { get; init; }`                                | SMBIOS Type 1 family.                                                                                                                                                                                                                                    |
| `String BaseboardManufacturer { get; init; }`                       | SMBIOS Type 2 baseboard manufacturer.                                                                                                                                                                                                                    |
| `String BaseboardProduct { get; init; }`                            | SMBIOS Type 2 baseboard product — the exact board identifier.                                                                                                                                                                                            |
| `String BaseboardVersion { get; init; }`                            | SMBIOS Type 2 baseboard version or revision.                                                                                                                                                                                                             |
| `String BiosVersion { get; init; }`                                 | System BIOS version string.                                                                                                                                                                                                                              |
| `String EcFirmwareVersion { get; init; }`                           | Embedded-controller firmware version, sourced from the vendor provider rather than SMBIOS.                                                                                                                                                               |
| `String McuFirmwareVersion { get; init; }`                          | Controller or MCU firmware version.                                                                                                                                                                                                                      |
| `String CpuIdentity { get; init; }`                                 | CPU family, model, and stepping in a normalized family-model-stepping form.                                                                                                                                                                              |
| `String ProcessorName { get; init; }`                               | Processor brand string, for example Intel(R) Core(TM) Ultra 7 258V.                                                                                                                                                                                      |
| `IReadOnlyList<UsbEndpointObservation> UsbEndpoints { get; init; }` | USB endpoints currently present on this machine.                                                                                                                                                                                                         |
| `IReadOnlyList<String> WmiProviderSignatures { get; init; }`        | Signatures of WMI providers, classes, or methods found present. Presence only. A method being enumerable never authorizes invoking it — the inventory records signatures precisely so a definition can gate on availability without anything calling it. |

## DeviceLogEntry

Namespace: `LibHandheld.Contracts`.

One native diagnostic transition. Never produced for each input sample. `Level`: Importance.
`Scope`: Native subsystem. `Message`: Plain diagnostic detail. `ChangeKey`: Optional key whose
unchanged repeats may be suppressed.

| Member                                                                                                | Contract                                                                                                                                                                                                                         |
| ----------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceLogEntry(DeviceLogLevel Level, String Scope, String Message, String ChangeKey = null/default)` | One native diagnostic transition. Never produced for each input sample. `Level`: Importance. `Scope`: Native subsystem. `Message`: Plain diagnostic detail. `ChangeKey`: Optional key whose unchanged repeats may be suppressed. |
| `DeviceLogLevel Level { get; init; }`                                                                 | Importance.                                                                                                                                                                                                                      |
| `String Scope { get; init; }`                                                                         | Native subsystem.                                                                                                                                                                                                                |
| `String Message { get; init; }`                                                                       | Plain diagnostic detail.                                                                                                                                                                                                         |
| `String ChangeKey { get; init; }`                                                                     | Optional key whose unchanged repeats may be suppressed.                                                                                                                                                                          |

## DeviceLogLevel

Namespace: `LibHandheld.Contracts`.

Diagnostic importance.

| Value   | Numeric value | Meaning                      |
| ------- | ------------- | ---------------------------- |
| `Info`  | `0`           | Normal transition.           |
| `Warn`  | `1`           | Degraded operation.          |
| `Error` | `2`           | Operation or service failed. |
| `Debug` | `3`           | Verbose developer detail.    |

## DeviceOperationalState

Namespace: `LibHandheld.Contracts`.

Aggregate state of native device services.

| Value      | Numeric value | Meaning                           |
| ---------- | ------------- | --------------------------------- |
| `Passive`  | `0`           | No mutable service acquired.      |
| `Active`   | `1`           | All required services started.    |
| `Degraded` | `2`           | Some services remain unavailable. |

## DeviceOperationResult

Namespace: `LibHandheld.Contracts`.

Result of a semantic request, including every completed native command in dispatch order. Validation
rejects the entire request before writing. Once dispatch begins, an unsuccessful or uncertain result
stops further commands. Earlier outcomes remain visible; no automatic retry or rollback occurs.

| Member                                                      | Contract                                                                                     |
| ----------------------------------------------------------- | -------------------------------------------------------------------------------------------- |
| `DeviceOperationResult()`                                   |                                                                                              |
| `CommandOutcome Outcome { get; init; }`                     | Aggregate outcome. Verified means every completed command was independently confirmed.       |
| `CapabilityReason Reason { get; init; }`                    | Why validation rejected the request or native execution stopped.                             |
| `IReadOnlyList<DeviceControlResult> Results { get; init; }` | Actual outcomes; empty when the request was refused before any write.                        |
| `Boolean Applied { get; }`                                  | Whether the complete request was written successfully, with or without independent readback. |

## DevicePowerMode

Namespace: `LibHandheld.Contracts`.

The Windows power mode requested by a device power preset, independent of power plans.

| Value             | Numeric value | Meaning                   |
| ----------------- | ------------- | ------------------------- |
| `BetterBattery`   | `0`           | Prefer battery life.      |
| `Balanced`        | `1`           | Use balanced performance. |
| `BestPerformance` | `2`           | Prefer performance.       |

## DevicePowerPreset

Namespace: `LibHandheld.Contracts`.

A device-authored shortcut for its sustained/slow power limits and Windows power mode. `Id`: Stable
identifier of ASCII letters, digits, dots, underscores or hyphens. `Name`: Single-line display name.
`SustainedWatts`: Target for the descriptor carrying this preset. `SlowWatts`: Target for the
single-instance PowerSlowLimit capability. `WindowsMode`: Windows power mode applied by the host.
Declare them on a single-instance sustained watt limit. Both targets must fit the current writable
descriptors. Presets are shortcuts, never policies to reapply after drift. Hosts derive Custom from
observed values; a failed multi-control application may be partial.

| Member                                                                                                          | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| --------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DevicePowerPreset(String Id, String Name, Int32 SustainedWatts, Int32 SlowWatts, DevicePowerMode WindowsMode)` | A device-authored shortcut for its sustained/slow power limits and Windows power mode. `Id`: Stable identifier of ASCII letters, digits, dots, underscores or hyphens. `Name`: Single-line display name. `SustainedWatts`: Target for the descriptor carrying this preset. `SlowWatts`: Target for the single-instance PowerSlowLimit capability. `WindowsMode`: Windows power mode applied by the host. Declare them on a single-instance sustained watt limit. Both targets must fit the current writable descriptors. Presets are shortcuts, never policies to reapply after drift. Hosts derive Custom from observed values; a failed multi-control application may be partial. |
| `String Id { get; init; }`                                                                                      | Stable identifier of ASCII letters, digits, dots, underscores or hyphens.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `String Name { get; init; }`                                                                                    | Single-line display name.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `Int32 SustainedWatts { get; init; }`                                                                           | Target for the descriptor carrying this preset.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| `Int32 SlowWatts { get; init; }`                                                                                | Target for the single-instance PowerSlowLimit capability.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `DevicePowerMode WindowsMode { get; init; }`                                                                    | Windows power mode applied by the host.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `Int32? FastWatts { get; init; }`                                                                               | Optional independent fast-PPT target; when omitted on a three-limit device, use SlowWatts.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `String ScenarioOnAc { get; init; }`                                                                            | Optional ScenarioMode choice applied before watt limits on AC power. Declare both scenario targets or neither. Choices belong to the single readable, writable ScenarioMode descriptor; the host must confirm the current power source.                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `String ScenarioOnDc { get; init; }`                                                                            | Optional ScenarioMode choice applied before watt limits on battery power.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `Boolean TryValidate(IReadOnlyList<CapabilityDescriptor> descriptors, String& error)`                           | Validates all presets against their current power descriptors. `descriptors`: The complete descriptor set. `error`: The first invalid declaration, if any. Whether the declarations are safe to offer.                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |

## DeviceStartResult

Namespace: `LibHandheld.Contracts`.

Result of native service acquisition. `State`: Aggregate native state. `Reason`: Optional diagnostic
explanation.

| Member                                                                     | Contract                                                                                                          |
| -------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------- |
| `DeviceStartResult(DeviceOperationalState State, CapabilityReason Reason)` | Result of native service acquisition. `State`: Aggregate native state. `Reason`: Optional diagnostic explanation. |
| `DeviceOperationalState State { get; init; }`                              | Aggregate native state.                                                                                           |
| `CapabilityReason Reason { get; init; }`                                   | Optional diagnostic explanation.                                                                                  |

## DeviceStopResult

Namespace: `LibHandheld.Contracts`.

Result of terminal native cleanup. `Status`: Restoration evidence. `Reason`: Optional diagnostic
explanation.

| Member                                                               | Contract                                                                                                      |
| -------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `DeviceStopResult(DeviceStopStatus Status, CapabilityReason Reason)` | Result of terminal native cleanup. `Status`: Restoration evidence. `Reason`: Optional diagnostic explanation. |
| `DeviceStopStatus Status { get; init; }`                             | Restoration evidence.                                                                                         |
| `CapabilityReason Reason { get; init; }`                             | Optional diagnostic explanation.                                                                              |

## DeviceStopStatus

Namespace: `LibHandheld.Contracts`.

Aggregate restoration evidence.

| Value        | Numeric value | Meaning                                                  |
| ------------ | ------------- | -------------------------------------------------------- |
| `Clean`      | `0`           | All changed temporary state restored and verified.       |
| `Unverified` | `1`           | Cleanup completed without full independent confirmation. |
| `Failed`     | `2`           | A restoration failed.                                    |

## DisplayKey

Namespace: `LibHandheld.Contracts`.

Shared semantic capability labels. Consumers choose localized text for these shared keys.

| Value                 | Numeric value | Meaning                                                                |
| --------------------- | ------------- | ---------------------------------------------------------------------- |
| `Custom`              | `0`           | Use LibHandheld.Contracts.CapabilityDisplay.CustomText as plugin text. |
| `Tdp`                 | `1`           | "TDP".                                                                 |
| `SustainedPowerLimit` | `2`           | "Sustained power limit".                                               |
| `BoostPowerLimit`     | `3`           | "Boost power limit".                                                   |
| `PerformanceProfile`  | `4`           | "Performance profile".                                                 |
| `FanMode`             | `5`           | "Fan mode".                                                            |
| `FanSpeed`            | `6`           | "Fan speed".                                                           |
| `FanCurve`            | `7`           | "Fan curve".                                                           |
| `FanLeft`             | `8`           | "Left fan".                                                            |
| `FanRight`            | `9`           | "Right fan".                                                           |
| `ChargeLimit`         | `10`          | "Charge limit".                                                        |
| `BypassCharging`      | `11`          | "Bypass charging".                                                     |
| `Lighting`            | `12`          | "Lighting".                                                            |
| `Brightness`          | `13`          | "Brightness".                                                          |
| `LightingEffect`      | `14`          | "Effect".                                                              |
| `LightingEffectSpeed` | `15`          | "Effect speed".                                                        |
| `CpuTemperature`      | `16`          | "CPU temperature".                                                     |
| `Battery`             | `17`          | "Battery".                                                             |
| `Controller`          | `18`          | "Controller".                                                          |
| `Motion`              | `19`          | "Motion".                                                              |
| `Rumble`              | `20`          | "Rumble".                                                              |
| `VariableRefreshRate` | `21`          | "Variable refresh rate".                                               |

## HandheldDefinition

Namespace: `LibHandheld.Contracts`.

A supported exact model and its stable family and glyph identities. `Id`: Stable model definition
identity. `FamilyId`: Stable family identity, also identifying existing recovery state. `Name`:
Plain model name. `GlyphProfileId`: Authored controller glyph profile identity.

| Member                                                                               | Contract                                                                                                                                                                                                                                                                          |
| ------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HandheldDefinition(String Id, String FamilyId, String Name, String GlyphProfileId)` | A supported exact model and its stable family and glyph identities. `Id`: Stable model definition identity. `FamilyId`: Stable family identity, also identifying existing recovery state. `Name`: Plain model name. `GlyphProfileId`: Authored controller glyph profile identity. |
| `String Id { get; init; }`                                                           | Stable model definition identity.                                                                                                                                                                                                                                                 |
| `String FamilyId { get; init; }`                                                     | Stable family identity, also identifying existing recovery state.                                                                                                                                                                                                                 |
| `String Name { get; init; }`                                                         | Plain model name.                                                                                                                                                                                                                                                                 |
| `String GlyphProfileId { get; init; }`                                               | Authored controller glyph profile identity.                                                                                                                                                                                                                                       |
| `Boolean HardwareVerified { get; init; }`                                            | Whether this exact model has a recorded hardware validation pass.                                                                                                                                                                                                                 |
| `IReadOnlyList<CapabilityRole> DeclaredRoles { get; init; }`                         | Semantic roles actually supplied by this model and its optional accessory services.                                                                                                                                                                                               |
| `Boolean HasController { get; init; }`                                               | Whether this implementation can acquire the model's physical controller.                                                                                                                                                                                                          |
| `DeviceHardwareAccess HardwareAccess { get; init; }`                                 | Privileged Windows access used by the model's concrete native controls.                                                                                                                                                                                                           |

## HandheldResources

Namespace: `LibHandheld.Contracts`.

Consumer-supplied locations of separately deployed native hardware helpers.

| Member                              | Contract                                                         |
| ----------------------------------- | ---------------------------------------------------------------- |
| `HandheldResources()`               |                                                                  |
| `String IntelKxPath { get; init; }` | The pinned Intel power helper, or null when it was not deployed. |
| `String InpOutPath { get; init; }`  | The pinned InpOut library, or null when it was not deployed.     |

## HapticActuatorSide

Namespace: `LibHandheld.Contracts`.

The actuator that renders a native haptic pulse.

| Value   | Numeric value | Meaning         |
| ------- | ------------- | --------------- |
| `Right` | `0`           | Right actuator. |
| `Left`  | `1`           | Left actuator.  |

## HapticCapabilities

Namespace: `LibHandheld.Contracts`.

What the physical device can do with output.

| Member                                              | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                              |
| --------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HapticCapabilities()`                              |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| `OutputChannelSupport LowFrequency { get; init; }`  | Support for the low-frequency motor.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| `OutputChannelSupport HighFrequency { get; init; }` | Support for the high-frequency motor.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
| `OutputChannelSupport LeftTrigger { get; init; }`   | Support for left trigger haptics.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                     |
| `OutputChannelSupport RightTrigger { get; init; }`  | Support for right trigger haptics.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `OutputChannelSupport NativePulse { get; init; }`   | Support for native actuator pulses, independent of motor envelopes.                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| `Int32 MaxFramesPerSecond { get; init; }`           | Highest output frame rate the device accepts, in frames per second.                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| `Single MinimumStartIntensity { get; init; }`       | Lowest intensity at which the device's motors reliably produce output, 0 to 1. Motor technology, declared by the plugin because the host cannot know it: a voice coil or LRA renders arbitrarily small intensities and keeps the default of zero, while an ERM motor does not start below roughly a third of full drive. The host maps bounded haptic events — not continuous rumble envelopes — onto this floor so LRA-grade ticks remain perceptible; scaling a faithful actuator by an ERM floor would be as wrong as the reverse. |
| `TimeSpan MinimumPulse { get; init; }`              | Shortest pulse the device's motors can render perceptibly. Zero for actuators with millisecond response; ERM motors need tens of milliseconds just to spin up. The host stretches bounded haptic events to at least this length and leaves continuous output untouched.                                                                                                                                                                                                                                                               |
| `HapticOutputFrame Clamp(HapticOutputFrame frame)`  | Drops channels the device cannot reproduce, leaving the rest untouched. `frame`: The frame as produced by the virtual target. A frame with unsupported channels zeroed and the original timestamp preserved. Supported intensities are neither range-clamped nor checked for finite values. Channels are dropped without redistributing their intensity to other motors.                                                                                                                                                              |

## HapticOutputFrame

Namespace: `LibHandheld.Contracts`.

One output frame travelling from the virtual target back to the physical device. The whole motor
state, like an input sample: a newer frame replaces an older one, so nothing needs to say which
target or generation it came from. A value type, because rumble streams a frame per change while a
game vibrates and the path that carries it allocates nothing per frame.

| Member                                             | Contract                                                                                                                                                                                                                                                                                                                      |
| -------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Single LowFrequency { get; init; }`               | Low-frequency motor intensity, from 0 to 1.                                                                                                                                                                                                                                                                                   |
| `Single HighFrequency { get; init; }`              | High-frequency motor intensity, from 0 to 1.                                                                                                                                                                                                                                                                                  |
| `Single LeftTrigger { get; init; }`                | Left trigger haptic intensity, from 0 to 1, where the device supports one.                                                                                                                                                                                                                                                    |
| `Single RightTrigger { get; init; }`               | Right trigger haptic intensity, from 0 to 1, where the device supports one.                                                                                                                                                                                                                                                   |
| `NativeHapticPulse? NativePulse { get; init; }`    | One native actuator pulse, delivered without replacing the current motor envelope.                                                                                                                                                                                                                                            |
| `DateTimeOffset Timestamp { get; init; }`          | When the frame was produced, in UTC.                                                                                                                                                                                                                                                                                          |
| `Boolean IsSilent { get; }`                        | Whether this frame commands no output at all.                                                                                                                                                                                                                                                                                 |
| `HapticOutputFrame Stop(DateTimeOffset timestamp)` | A frame that stops all output. `timestamp`: When the stop was issued. A frame with every channel at zero. Rumble always needs an explicit stop path. A motor left running is not a cosmetic bug: it keeps vibrating after the game closed, the overlay opened, or the plugin was disabled, and nothing else will turn it off. |

## HapticPulseStyle

Namespace: `LibHandheld.Contracts`.

The native pulse waveform.

| Value      | Numeric value | Meaning                |
| ---------- | ------------- | ---------------------- |
| `Disabled` | `0`           | Stop the actuator.     |
| `Weak`     | `1`           | Weak pulse waveform.   |
| `Strong`   | `2`           | Strong pulse waveform. |

## HardwareStateQuality

Namespace: `LibHandheld.Contracts`.

How much a reported hardware value can be trusted. Availability is independent of value quality. An
accepted control write supplies LibHandheld.Contracts.HardwareStateQuality.Observed state for the
owning cycle without a readback or verification pass. Sensor measurements retain their actual
sampling time and may become stale independently.

| Value      | Numeric value | Meaning                                                                                    |
| ---------- | ------------- | ------------------------------------------------------------------------------------------ |
| `Unknown`  | `0`           | No value has been sampled or successfully written; an available control remains usable.    |
| `Observed` | `1`           | Read from hardware or published after an accepted write, without independent confirmation. |
| `Verified` | `2`           | Read back and confirmed to match what was applied.                                         |
| `Stale`    | `3`           | Was valid, but the observation has expired or its generation is gone.                      |
| `Faulted`  | `4`           | The capability failed and its value cannot be trusted at all.                              |

## IHandheldObserver

Namespace: `LibHandheld.Contracts`.

Typed publication consumer; callbacks acknowledge dispatch, not downstream delivery. Collections are
detached replacements. Controller samples are complete value types; do not log or allocate per
sample.

| Member                                                                                                                                                 | Contract                                                                    |
| ------------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------- |
| `ValueTask DescriptorsChangedAsync(CapabilityDescriptorSet descriptors, CancellationToken cancellationToken)`                                          | Accepts a complete descriptor replacement.                                  |
| `ValueTask StateChangedAsync(CapabilityState state, CancellationToken cancellationToken)`                                                              | Accepts one observed control state.                                         |
| `ValueTask PhysicalDevicesChangedAsync(IReadOnlyList<PhysicalDeviceIdentity> devices, HapticCapabilities output, CancellationToken cancellationToken)` | Accepts a complete physical interface replacement and its haptic mechanics. |
| `ValueTask ControllerSampleAsync(CanonicalControllerSample sample, CancellationToken cancellationToken)`                                               | Accepts complete canonical physical input and motion.                       |
| `ValueTask OemControlsChangedAsync(IReadOnlyList<OemControlDescriptor> controls, CancellationToken cancellationToken)`                                 | Accepts a complete assignable OEM control replacement.                      |
| `ValueTask OemEventAsync(OemControlEvent controlEvent, CancellationToken cancellationToken)`                                                           | Accepts a deduplicated OEM press or release.                                |
| `void Faulted(String scope, String message)`                                                                                                           | Reports a background fault invalidating this device cycle.                  |

## MotionSample

Namespace: `LibHandheld.Contracts`.

One motion sample. Gyroscope and accelerometer are separate and optional because a device may have
one without the other, or its operating-system sensor stack may project only one of them.
Synthesizing a missing sensor would invent data.

| Member                                           | Contract                                                                      |
| ------------------------------------------------ | ----------------------------------------------------------------------------- |
| `Single GyroX { get; init; }`                    | Angular velocity around X, in degrees per second.                             |
| `Single GyroY { get; init; }`                    | Angular velocity around Y, in degrees per second.                             |
| `Single GyroZ { get; init; }`                    | Angular velocity around Z, in degrees per second.                             |
| `Boolean HasGyro { get; init; }`                 | Whether the gyroscope values are present.                                     |
| `Single AccelX { get; init; }`                   | Acceleration along X, in g.                                                   |
| `Single AccelY { get; init; }`                   | Acceleration along Y, in g.                                                   |
| `Single AccelZ { get; init; }`                   | Acceleration along Z, in g.                                                   |
| `Boolean HasAccelerometer { get; init; }`        | Whether the accelerometer values are present.                                 |
| `DateTimeOffset? SensorTimestamp { get; init; }` | Sensor timestamp, when the sensor supplies one distinct from the sample time. |

## NativeHapticPulse

Namespace: `LibHandheld.Contracts`.

A native pulse with a signed, device-defined intensity. `Side`: Actuator that renders the pulse.
`Style`: Native waveform, or Disabled to stop that actuator. `Intensity`: Signed native intensity;
Neptune button feedback ranges from -7 to 5.

| Member                                                                                                  | Contract                                                                                                                                                                                                                                                                                                     |
| ------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `NativeHapticPulse(HapticActuatorSide Side, HapticPulseStyle Style, SByte Intensity)`                   | A native pulse with a signed, device-defined intensity. `Side`: Actuator that renders the pulse. `Style`: Native waveform, or Disabled to stop that actuator. `Intensity`: Signed native intensity; Neptune button feedback ranges from -7 to 5.                                                             |
| `HapticActuatorSide Side { get; init; }`                                                                | Actuator that renders the pulse.                                                                                                                                                                                                                                                                             |
| `HapticPulseStyle Style { get; init; }`                                                                 | Native waveform, or Disabled to stop that actuator.                                                                                                                                                                                                                                                          |
| `SByte Intensity { get; init; }`                                                                        | Signed native intensity; Neptune button feedback ranges from -7 to 5.                                                                                                                                                                                                                                        |
| `NativeHapticPulse ForButton(CanonicalButtons button, ButtonHapticStrength strength, Boolean released)` | Creates Neptune button feedback, including its reversed actuator and softer release pulse. `button`: One physical canonical button. `strength`: Requested press feedback strength. `released`: Whether this is the softer release feedback. A pulse with the native actuator, waveform and signed intensity. |

## OemControlDefaultActionHint

Namespace: `LibHandheld.Contracts`.

A physical control's semantic default, independent of an application or model name.

| Value                  | Numeric value | Meaning                                                          |
| ---------------------- | ------------- | ---------------------------------------------------------------- |
| `None`                 | `0`           | No suggested action.                                             |
| `CompanionApplication` | `1`           | Open the consuming companion application.                        |
| `MouseSecondaryButton` | `2`           | Hold the secondary mouse button for the physical press duration. |

## OemControlDescriptor

Namespace: `LibHandheld.Contracts`.

A logical OEM control published by the plugin. A separate channel from the gamepad, deliberately.
Face buttons, sticks, triggers, and the D-pad are not expressible here, so a plugin can publish the
physical vendor controls without supplying host assignment policy or turning the canonical gamepad
channel into a remapper.

| Member                                                         | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| -------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `OemControlDescriptor()`                                       |                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `String ControlId { get; init; }`                              | Stable identifier within the device definition, for example oem1.                                                                                                                                                                                                                                                                                                                                                                                                  |
| `CapabilityDisplay Display { get; init; }`                     | How WSGM labels it.                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `OemControlPlacement Placement { get; init; }`                 | Where the control physically is, which decides what may be bound to it.                                                                                                                                                                                                                                                                                                                                                                                            |
| `Boolean SupportsLongPress { get; init; }`                     | Whether the source distinguishes a short press from a long one.                                                                                                                                                                                                                                                                                                                                                                                                    |
| `Boolean RequiresControllerAcquisition { get; init; }`         | Whether this control disappears when WSGM controller management is turned off. Declared by the plugin rather than inferred: only it knows whether a control rides on the physical-controller resource. On the reference handheld the rear paddles do — they are visible only in the acquisition mode the plugin selects — while the front buttons arrive over a separate vendor event channel and survive.                                                         |
| `Boolean CompanionApplication { get; init; }`                  | Whether this is the button the manufacturer prints for its own companion application, such as Armoury Crate or Legion Space. Physical metadata, like LibHandheld.Contracts.OemControlDescriptor.Placement: it says what the button is for on the device, not what WSGM does with it. The host decides what an unassigned control of this kind does. A plugin that already routes the button into the controller sample as Guide or Quick Access leaves this false. |
| `OemControlDefaultActionHint DefaultActionHint { get; init; }` | Suggested semantic behavior; the consuming application's explicit assignment wins.                                                                                                                                                                                                                                                                                                                                                                                 |
| `CanonicalButtons ControllerButton { get; init; }`             | The Guide or Quick Access bit this OEM control also forwards through the controller channel.                                                                                                                                                                                                                                                                                                                                                                       |

## OemControlEdge

Namespace: `LibHandheld.Contracts`.

The physical edge represented by an OEM event.

| Value      | Numeric value | Meaning                                                      |
| ---------- | ------------- | ------------------------------------------------------------ |
| `Pressed`  | `0`           | The control became pressed.                                  |
| `Released` | `1`           | The control was released and any held-state guard may reset. |

## OemControlEvent

Namespace: `LibHandheld.Contracts`.

One published OEM control event. `ControlId`: The control that was pressed. `Press`: Which press
duration was observed. `Timestamp`: When it was observed, in UTC. `DeduplicationId`: Identifier that
is equal across every source reporting the same physical press. `Edge`: Whether this event
represents the press or release edge. The deduplication ID exists because one press can legitimately
arrive twice: a vendor event channel and a raw-input path may both see it. Without a shared
identifier WSGM would toggle the QAM open and closed on a single press.

| Member                                                                                                                                   | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| ---------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `OemControlEvent(String ControlId, OemPressKind Press, DateTimeOffset Timestamp, String DeduplicationId, OemControlEdge Edge = Pressed)` | One published OEM control event. `ControlId`: The control that was pressed. `Press`: Which press duration was observed. `Timestamp`: When it was observed, in UTC. `DeduplicationId`: Identifier that is equal across every source reporting the same physical press. `Edge`: Whether this event represents the press or release edge. The deduplication ID exists because one press can legitimately arrive twice: a vendor event channel and a raw-input path may both see it. Without a shared identifier WSGM would toggle the QAM open and closed on a single press. |
| `String ControlId { get; init; }`                                                                                                        | The control that was pressed.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `OemPressKind Press { get; init; }`                                                                                                      | Which press duration was observed.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| `DateTimeOffset Timestamp { get; init; }`                                                                                                | When it was observed, in UTC.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `String DeduplicationId { get; init; }`                                                                                                  | Identifier that is equal across every source reporting the same physical press.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| `OemControlEdge Edge { get; init; }`                                                                                                     | Whether this event represents the press or release edge.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |

## OemControlPlacement

Namespace: `LibHandheld.Contracts`.

Where an OEM control sits on the device. Placement is physical metadata. The host owns every action
vocabulary and decides whether a mapping is compatible with that placement; plugins never publish
host application policy.

| Value   | Numeric value | Meaning                                                             |
| ------- | ------------- | ------------------------------------------------------------------- |
| `Front` | `0`           | A front-facing vendor button, such as a home or quick-settings key. |
| `Rear`  | `1`           | A rear paddle or grip control.                                      |

## OemPressKind

Namespace: `LibHandheld.Contracts`.

Which press duration an assignment applies to.

| Value   | Numeric value | Meaning                                           |
| ------- | ------------- | ------------------------------------------------- |
| `Short` | `0`           | A short press.                                    |
| `Long`  | `1`           | A long press, where the source distinguishes one. |

## OutputChannelSupport

Namespace: `LibHandheld.Contracts`.

What a device does with an output channel it cannot reproduce. Declared per channel so the plugin's
answer is visible rather than implied. A device with two rumble motors and no trigger haptics should
say so; silently discarding trigger output looks identical to a broken implementation from the
outside.

| Value         | Numeric value | Meaning                                                 |
| ------------- | ------------- | ------------------------------------------------------- |
| `Native`      | `0`           | The device drives this channel directly.                |
| `Unsupported` | `1`           | The channel is not present and its output is discarded. |

## PhysicalDeviceIdentity

Namespace: `LibHandheld.Contracts`.

A physical input device owned by a plugin and eligible for WSGM's HidHide transaction.

| Member                                  | Contract                                                             |
| --------------------------------------- | -------------------------------------------------------------------- |
| `PhysicalDeviceIdentity()`              |                                                                      |
| `String InstancePath { get; init; }`    | The device instance path, used verbatim as the HidHide entry.        |
| `String LocationPath { get; init; }`    | Physical USB location, for correlating across re-enumeration.        |
| `String VendorId { get; init; }`        | USB vendor ID, four uppercase hexadecimal digits.                    |
| `String ProductId { get; init; }`       | USB product ID, four uppercase hexadecimal digits.                   |
| `Boolean RequiresHiding { get; init; }` | Whether hiding this interface is required for controller management. |

## PowerLimits

Namespace: `LibHandheld.Contracts`.

Explicit processor power targets. Omitted limits are left unchanged. `SustainedWatts`: Sustained
processor power in watts. `SlowWatts`: Optional slow-window power in watts. `FastWatts`: Optional
fast-window power in watts. `PeakWatts`: Optional peak power in watts. A declared native power pair
requires both targets; use SetTdpAsync for one uniform target.

| Member                                                                                                                                 | Contract                                                                                                                                                                                                                                                                                                                                                              |
| -------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PowerLimits(Int32 SustainedWatts, Int32? SlowWatts = null/default, Int32? FastWatts = null/default, Int32? PeakWatts = null/default)` | Explicit processor power targets. Omitted limits are left unchanged. `SustainedWatts`: Sustained processor power in watts. `SlowWatts`: Optional slow-window power in watts. `FastWatts`: Optional fast-window power in watts. `PeakWatts`: Optional peak power in watts. A declared native power pair requires both targets; use SetTdpAsync for one uniform target. |
| `Int32 SustainedWatts { get; init; }`                                                                                                  | Sustained processor power in watts.                                                                                                                                                                                                                                                                                                                                   |
| `Int32? SlowWatts { get; init; }`                                                                                                      | Optional slow-window power in watts.                                                                                                                                                                                                                                                                                                                                  |
| `Int32? FastWatts { get; init; }`                                                                                                      | Optional fast-window power in watts.                                                                                                                                                                                                                                                                                                                                  |
| `Int32? PeakWatts { get; init; }`                                                                                                      | Optional peak power in watts.                                                                                                                                                                                                                                                                                                                                         |

## UsbEndpointObservation

Namespace: `LibHandheld.Contracts`.

One observed USB endpoint.

| Member                                              | Contract                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| --------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `UsbEndpointObservation()`                          |                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| `String VendorId { get; init; }`                    | USB vendor ID, four uppercase hexadecimal digits.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `String ProductId { get; init; }`                   | USB product ID, four uppercase hexadecimal digits.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| `Int32? InterfaceNumber { get; init; }`             | Interface number when this endpoint is one interface of a composite device.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| `String DeviceRelease { get; init; }`               | USB bcdDevice, four uppercase hexadecimal digits.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `String ReportDescriptorHash { get; init; }`        | Hash of this endpoint's HID report descriptor.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                             |
| `IReadOnlyList<Int32> ReportLengths { get; init; }` | Observed HID report lengths in bytes.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| `String LocationPath { get; init; }`                | Physical USB location path, used to follow this endpoint across re-enumeration. Deliberately diagnostic-only and unusable as a package-manifest predicate. A location path describes which port a device is plugged into on _this_ machine and differs between units of the same model, so gating on it would match one developer's unit and nothing else. It is the continuation key for hotplug and controller mode changes — the only identifier verified stable across a full mode-switch cycle, since container ID is the null GUID on the reference hardware and the USB serial exists in only one of the two modes. |

## HandheldDevice

Namespace: `LibHandheld`.

Owns one exact handheld's native services and semantic controls.

| Member                                                                                                                                                                                                                  | Contract                                                                                                                                                                                                                                             |
| ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceControlSnapshot Controls { get; }`                                                                                                                                                                               | Current native controls and observations, without retaining mutable engine collections.                                                                                                                                                              |
| `IReadOnlyList<HandheldDefinition> Definitions { get; }`                                                                                                                                                                | Every supported exact model.                                                                                                                                                                                                                         |
| `HandheldDefinition Definition { get; }`                                                                                                                                                                                | The exact model selected from the supplied identity.                                                                                                                                                                                                 |
| `IHandheldObserver Observer { get; set; }`                                                                                                                                                                              | Borrowed publication observer, never disposed by the device.                                                                                                                                                                                         |
| `IReadOnlyList<String> GlyphResources { get; }`                                                                                                                                                                         | Authored glyph paths and attribution notice derived from this definition's embedded glyph profile.                                                                                                                                                   |
| `ValueTask<DeviceOperationResult> SetTdpAsync(Int32 watts, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                                      | Sets one uniform watt target on the selected model's sustained and declared boost limits. Native providers own the appropriate firmware order; no model identifiers or transport details are needed.                                                 |
| `ValueTask<DeviceOperationResult> SetPowerLimitsAsync(PowerLimits limits, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                       | Sets explicit sustained, slow, fast and peak targets where the model declares them. All bounds and pair relationships are checked before the first write. Omitted limits are not inferred from another target.                                       |
| `ValueTask<DeviceOperationResult> SetScenarioModeAsync(String mode, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                             | Sets a declared native performance mode. Available option names come from Controls.                                                                                                                                                                  |
| `ValueTask<DeviceOperationResult> SetFanModeAsync(String mode, String fan = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                       | Sets the declared fan mode for one logical fan, or the sole shared fan control.                                                                                                                                                                      |
| `ValueTask<DeviceOperationResult> SetFanDutyAsync(Int32 percent, String fan = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                     | Sets a fan duty percentage where that logical fan exposes direct duty control.                                                                                                                                                                       |
| `ValueTask<DeviceOperationResult> SetFanTargetRpmAsync(Int32 rpm, String fan = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                    | Sets a target RPM where the selected model exposes RPM control.                                                                                                                                                                                      |
| `ValueTask<DeviceOperationResult> SetFanCurveAsync(IReadOnlyList<CurvePoint> points, String fan = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                 | Sets a declared fan curve. Inputs are Celsius; outputs use the control's declared unit and bounds.                                                                                                                                                   |
| `ValueTask<DeviceOperationResult> SetChargeLimitAsync(Int32 percent, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                            | Sets the battery charge ceiling using the selected model's real charging mechanism.                                                                                                                                                                  |
| `ValueTask<DeviceOperationResult> SetChargeProtectionAsync(Boolean enabled, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                     | Enables or disables a declared Boolean battery-protection control.                                                                                                                                                                                   |
| `ValueTask<DeviceOperationResult> SetChargeProtectionModeAsync(String mode, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                     | Sets a declared choice battery-protection mode; choices are discoverable through Controls.                                                                                                                                                           |
| `ValueTask<DeviceOperationResult> SetChargeBypassModeAsync(String mode, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                         | Sets a declared bypass-charging mode; choices are discoverable through Controls.                                                                                                                                                                     |
| `ValueTask<DeviceOperationResult> SetChargeBypassAsync(Boolean enabled, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                                         | Enables or disables real bypass charging using a Boolean control or declared always/disabled choices. Other choice shapes are refused; use SetChargeBypassModeAsync for an explicit mode. A charge ceiling is never substituted for bypass.          |
| `ValueTask<DeviceOperationResult> SetLightingPowerAsync(Boolean enabled, String zone = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                            | Enables or disables a declared lighting power control.                                                                                                                                                                                               |
| `ValueTask<DeviceOperationResult> SetLightingBrightnessAsync(Int32 percent, String zone = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                         | Sets declared lighting brightness. A logical zone is required when the model exposes several.                                                                                                                                                        |
| `ValueTask<DeviceOperationResult> SetLightingColorAsync(Int32 rgb, String zone = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                  | Sets the 24-bit RGB color of the selected logical lighting zone.                                                                                                                                                                                     |
| `ValueTask<DeviceOperationResult> SetLightingEffectAsync(String effect, String zone = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                             | Sets a declared lighting effect. The model's option names are exposed in Controls.                                                                                                                                                                   |
| `ValueTask<DeviceOperationResult> SetLightingSpeedAsync(Int32 speed, String zone = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)`                                | Sets a declared lighting effect speed, using its published numeric bounds.                                                                                                                                                                           |
| `ValueTask<DeviceOperationResult> SetControlAsync(CapabilityRole role, CapabilityValue value, String instanceId = null/default, Deadline? deadline = null/default, CancellationToken cancellationToken = null/default)` | Sets one semantic role without exposing a capability identifier or a device-specific transport. Ambiguous roles require a logical instance; unsupported operations are refused before dispatch.                                                      |
| `ValueTask DisposeAsync()`                                                                                                                                                                                              | Releases handles only. StopAsync owns hardware restoration.                                                                                                                                                                                          |
| `DeviceIdentitySnapshot ReadIdentity()`                                                                                                                                                                                 | Captures Windows registry identity, processor facts and read-only USB interface observations once. No firmware commands, feature-report probes or privileged drivers are used; absent EC and MCU facts remain unknown.                               |
| `HandheldDefinition Detect(DeviceIdentitySnapshot identity)`                                                                                                                                                            | Matches supplied observations without opening a transport or reading the machine again.                                                                                                                                                              |
| `HandheldDevice Create(HandheldDefinition definition, DeviceIdentitySnapshot identity, String stateDirectory, Action<DeviceLogEntry> diagnostics = null/default, HandheldResources resources = null/default)`           | Creates services for a matched exact model; acquisition begins at StartAsync.                                                                                                                                                                        |
| `ValueTask<DeviceStartResult> StartAsync(Boolean controllerManagementEnabled = True, CancellationToken cancellationToken = null/default)`                                                                               | Acquires native services; physical controller ownership is independently optional.                                                                                                                                                                   |
| `ValueTask SuspendAsync(Deadline deadline, CancellationToken cancellationToken = null/default)`                                                                                                                         | Quiesces native services for suspend.                                                                                                                                                                                                                |
| `ValueTask<DeviceStartResult> ResumeAsync(Deadline deadline, CancellationToken cancellationToken = null/default)`                                                                                                       | Reacquires services using the original exact identity snapshot.                                                                                                                                                                                      |
| `ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, CancellationToken cancellationToken = null/default)`                                                                                        | Executes a typed vendor control without an application-specific adapter.                                                                                                                                                                             |
| `ValueTask SetControllerManagementAsync(Boolean enabled, Deadline deadline, CancellationToken cancellationToken = null/default)`                                                                                        | Changes physical input ownership while other hardware services continue.                                                                                                                                                                             |
| `ValueTask ReleaseControllerAsync(Deadline deadline, CancellationToken cancellationToken = null/default)`                                                                                                               | Releases physical input and restores the native controller baseline.                                                                                                                                                                                 |
| `ValueTask ApplyHapticsAsync(HapticOutputFrame frame, CancellationToken cancellationToken = null/default)`                                                                                                              | Delivers complete motor state; zero requests an explicit stop.                                                                                                                                                                                       |
| `ValueTask ApplyButtonHapticsAsync(CanonicalButtons button, ButtonHapticStrength strength, Boolean released, CancellationToken cancellationToken = null/default)`                                                       | Delivers a native button pulse without replacing ongoing virtual-controller rumble. Call only when the published output capabilities declare native pulse support.                                                                                   |
| `ValueTask<IReadOnlyDictionary<String, String>> GetDiagnosticsAsync(CancellationToken cancellationToken = null/default)`                                                                                                | Returns current native diagnostics.                                                                                                                                                                                                                  |
| `ValueTask<DeviceStopResult> StopAsync(Deadline deadline, CancellationToken cancellationToken = null/default)`                                                                                                          | Restores captured originals and releases native ownership.                                                                                                                                                                                           |
| `Stream OpenGlyphResource(String relativePath)`                                                                                                                                                                         | Opens original authored bytes for an allowlisted path. The caller validates content and disposes the stream. `relativePath`: One path in GlyphResources, compared ordinally. The original embedded bytes; no parsing, rewriting or execution occurs. |

## Processor tuning reset

Selected devices with processor controls expose the `processor.reset` action. Call it without a
value after removing the consumer's saved processor preferences. The action restores supported
automatic GPU clock modes, zero curve/undervolt offsets and readable captured originals. Its
published state and result reason list settings requiring a Windows restart when no live restore
protocol or original exists. No factory thermal values are guessed. A failed submitted write stops
the reset with an indeterminate outcome and is never retried. Suspend/resume keeps track of settings
still awaiting firmware reset for the lifetime of the device instance.
