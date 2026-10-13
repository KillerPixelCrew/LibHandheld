# Input, haptics and glyphs

Assign a borrowed IHandheldObserver before starting. The device never disposes it. Keep callbacks
short and marshal UI work to the consumer's context.

| Callback                    | Publication                                             |
| --------------------------- | ------------------------------------------------------- |
| DescriptorsChangedAsync     | Complete descriptor replacement                         |
| StateChangedAsync           | One control observation                                 |
| PhysicalDevicesChangedAsync | Complete physical-interface set and haptic capabilities |
| ControllerSampleAsync       | Complete canonical input and optional motion            |
| OemControlsChangedAsync     | Complete OEM control metadata replacement               |
| OemEventAsync               | Deduplicated OEM press/release                          |
| Faulted                     | Background fault invalidating the active device cycle   |

Empty replacements remove previous descriptors/interfaces. Stop consumer forwarding on faults and
prevent old reports reviving a retired cycle. High-rate callbacks should allocate and log nothing
per sample in the consumer.

CanonicalControllerSample carries complete held-button state. Sticks and pads range from -1 to 1
with positive Y up. Triggers, contact strength and touch pressure range from 0 to 1. MotionSample
uses degrees per second for gyro and g for acceleration. Presence flags distinguish absent sensors
from zero motion; SensorTimestamp can differ from the controller timestamp. Native axis transforms
belong to the library. The host maps canonical reports to its target and neutralizes it when
forwarding ends.

OEM descriptors supply physical identity, placement, press kind, default-action hints and an
optional ControllerButton alias. Guide/Quick Access can appear through both event and canonical
paths; prevent duplicate dispatch. Assignments and Steam/overlay actions belong to the host.

## Haptics

ApplyHapticsAsync takes complete HapticOutputFrame motor state with 0–1 amplitudes. Inspect
HapticCapabilities; not every actuator exists. New frames replace the motor envelope; NativePulse
adds supported actuator feedback. ApplyButtonHapticsAsync supplies model-supported button feedback.

Use one output owner for virtual rumble and previews. Send HapticOutputFrame.Stop with a current
timestamp when ending output. Output uncertainty may disable haptics while input remains healthy;
recover through an explicit lifecycle/controller-management action.

## Glyphs

Definition.GlyphProfileId selects embedded artwork. Enumerate GlyphResources and pass an allowlisted
path to OpenGlyphResource. Dispose the returned stream. The library returns original bytes and does
not parse, rewrite or execute them; validate them in your renderer. Preserve accompanying family,
font and artwork notices. A glyph failure does not change hardware selection.
