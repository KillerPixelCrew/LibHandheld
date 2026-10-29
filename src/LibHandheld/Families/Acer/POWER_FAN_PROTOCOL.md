# Predator Sense power and fan protocol research

Offline investigation, 2026-10-27, of the maintainer-supplied Predator Sense 1.0.1563.0 package.
This records binary contracts for a future native implementation. It does not add runtime controls
or establish hardware acceptance. No Acer program was executed and no service or device was opened.

## Source identities

All native addresses below are image virtual addresses in these exact files under
`drivers/AcerAgentServiceHH/swc/`:

| File                    | SHA-256                                                            |
| ----------------------- | ------------------------------------------------------------------ |
| AcerHardwareService.exe | `3aad1b07ef5b22d11505fd9193873ebd5acb87cf29d68556ef83debfeb7fce36` |
| AcerAgentService.exe    | `12fb11542015e556da3d50bd4bf0eb6260a62186042cdd141a9ea7505eccf819` |

Managed desktop callers were extracted with ILSpy. Native contracts were cross-checked with objdump
and REA 6.8.0 using Ghidra 12.1.4 default analysis. PDB source was not available; decompiled types
and temporary variable names are inferred. Proprietary binaries/decompiled code are not
redistributed with LibHandheld.

## Power: separate EC HID transport

HardwareService `140018a90` selects VID `1025`, PID `174B`, usage page `FF05`, usage `0001`. The
comparison callbacks are `14001c9e0` (VID/PID) and `14001c990` (usage page/usage). This is separate
from the `0502:F901` gamepad.

`140017e00` and `140018140` send/receive feature report `A0` using the descriptor's feature-report
length. They prepend/remove the report ID and copy at most 64 payload bytes. Do not assume that the
collection's feature length is 64 without its descriptor.

The following offsets exclude the report ID: payload offset 0 is raw Windows report offset 1. All
multibyte fields are little endian. A request starts with word `A000`, followed by a command word at
offset 2. Responses start with result word `E000` for success and echo the command at offset 2.
SetFeature success alone does not establish firmware acceptance.

| Operation              | Payload bytes/fields                                          | Native owner                       |
| ---------------------- | ------------------------------------------------------------- | ---------------------------------- |
| Get data version       | `00 A0 00 00 00`                                              | `140019390`                        |
| Get supported profiles | `00 A0 00 00 04`                                              | `140019840`, called by `14000dee0` |
| Get current mode       | `00 A0 01 00 02`                                              | `140019df0`                        |
| Select mode            | `00 A0 01 00 01 mode`                                         | `14001a330`                        |
| Read profile           | `00 A0 02 00 02 profile`                                      | `14001a860`                        |
| Write profile          | `00 A0 02 00 01 profile`, followed by retained profile fields | `14001adb0`                        |
| Read profile limit     | `00 A0 02 00 03 profile`; selector at payload offset 25       | `14001b370`                        |

The supported-profile result is a 16-bit field at payload offset 4 for the old version branch. The
newer branch requires type byte 4 at offset 4 and reads the field at offset 6. The version branch
condition is `(version & 0xff) != 0 || ((version >> 8) & 0xff) > 5`. Keep that source condition; do
not invent a semantic firmware version ordering. `GetDataVersion` itself has old/new response
handling, so it must be retained when implementing this protocol.

Profile readback has profile ID at offset 5, PL1 at offset 6 (uint16), and PL2 at offset 10
(uint16). Other retained fields occupy offsets 8, 12, 14 (uint16), 16, 17, 18 (byte), and 19, 21, 23
(uint16). Their meanings are not established. They are copied by the profile writer; they must not
be replaced by invented defaults or zeroed because a read failed.

For limit reads, the response echoes the selector at payload offset 25 and returns a uint16 at
offset 26. The desktop capability fields trace through `14000dee0` and callbacks `14001c8a0`,
`14001c8f0`, `14001c940`:

| Profile 3 selector | Desktop meaning |
| ------------------ | --------------- |
| 1                  | PL1 minimum     |
| 2                  | PL1 maximum     |
| 4                  | PL2 maximum     |

Power modes are Turbo 0, Balance 1, Quiet 2 and Manual 3. The managed boundary uses integer watts;
HardwareService `14000e9f0` assigns the two input integers directly to profile PL1 and PL2 and
`14001adb0` narrows them to uint16 fields, without a visible unit multiplier. Validate before
encoding; do not reproduce truncation of arbitrary input.

The selector labels above correct the initial offline interpretation. On the V1.03 unit, selectors
1/2/4 returned 10/40/65. Reinspection of the same HardwareService binary traces the first limit
callback through vtable 140039890 to 14001C940 (selector 1), the second through 1400398C8 to
14001C8F0 (selector 2), and the third through 140039900 to 14001C8A0 (selector 4).

Manual dispatch reads profile 3, changes its PL1/PL2 fields, writes it, then selects mode 3. Preset
dispatch only selects the requested mode. The vendor still attempts later steps if earlier steps
fail and combines their boolean results afterwards. That sequencing is not evidence of transactional
success: a library implementation must report partial/uncertain effects honestly, preserve unrelated
profile fields and never automatically repeat an uncertain write.

The desktop fallback bounds are PL1 10..40 W and PL2 15..65 W, with defaults 35/65 W. These are
application defaults, not measured Atlas limits. Use the device's capability values. The desktop
requires PL2 greater than PL1 and offers no source-grounded preset wattage table in these callers.

Each EC transaction uses a process lock and a global mutex named
`Global\ECHIDFeatureVID_1025&PID_174B`, with SetFeature, 20 ms delay, GetFeature, then release. The
delay helper `140011a10` confirms millisecond units. Mutex wait is 1000 ms at `1400191d0`; the
vendor logs timeouts and continues. A library must honor ownership failure rather than copy that
behavior. Do not hold a .NET thread-affine mutex across an await that can change threads.

## Fans: AcerGamingFunction WMI

The inspected `FAN_CONTROL` setters do not use the EC HID power transport. Agent wrappers
`140018180` and `1400185d0` forward service commands `0D` and `0F` to HardwareService.

Namespace `ROOT\WMI`, class `AcerGamingFunction`:

| Method               | gmInput               | gmOutput      | HardwareService helper   |
| -------------------- | --------------------- | ------------- | ------------------------ |
| SetGamingFanBehavior | uint64 packed value   | uint32 status | `140021260`              |
| SetGamingFanSpeed    | uint64 packed value   | uint32 status | `1400216a0`              |
| GetGamingFanBehavior | uint32 selection mask | uint64 result | Agent decode `140033c40` |
| GetGamingFanSpeed    | uint32 speed selector | uint64 result | Agent decode `140033c40` |

The native setter converts the uint64 to a numeric BSTR for WMI transport and converts gmOutput to
uint32. It does not unpack the fan fields. Invocation/conversion failure becomes `FFFFFFFF`;
successful invocation receives 30 ms pacing. The upper agent checks the low byte for zero; retain
the full status in diagnostics rather than discarding high bits without a firmware contract.

Managed modes are Auto 0, Max 1, Custom 2. Native behavior values are Auto 1, Max 2, Manual 3.
Behavior writes pack `(twoBitBehaviors << 16) | selectedFanMask`:

| Fan                  | Selection bit | Two-bit behavior shift | Fixed-speed selector |
| -------------------- | ------------- | ---------------------- | -------------------- |
| CPU                  | 0             | 0                      | 1                    |
| SYS, SYS2 (j = 0, 1) | j + 1         | 2 + 2j                 | j + 2                |
| GPU, GPU2 (j = 0, 1) | j + 3         | 6 + 2j                 | j + 4                |

Fixed-speed writes pack `(speed << 8) | selector`. The parsed integer is forwarded without a visible
scale conversion. For example, CPU manual behavior is `00030001`, and CPU speed 50 is `00003201`;
these are static encoding examples, not commands sent to hardware.

The getter's reply layout differs from the setter's input. Agent `140033c40` rejects either getter
when the returned low byte is nonzero. Behavior readback uses `(reply >> 8) & 3` for CPU,
`(reply >> (10 + 2j)) & 3` for SYS and `(reply >> (14 + 2j)) & 3` for GPU. Speed readback uses
`(reply >> 8) & 0xff`, with the speed selectors above. Capture each fan's actual behavior and speed
for restoration; do not treat a behavior getter reply as an already encoded setter request.

The vendor UI's global fan mode prefers a cached registry `CurrentFanMode` under
`HKLM\SOFTWARE\OEM\AcerAgentService\FanControl` (`140034fee`..`140035053`). That preference can
disagree with firmware or another controller's changes. Without the cache, any observed manual
behavior selects Custom 2; otherwise the getter selects Auto 0. It cannot reconstruct Max 1. Per-fan
projection also maps other nonmanual behaviors to auto and substitutes speed zero after a failed
speed getter. Retain raw per-fan values and known/unknown state for restoration.

Custom mode groups per-fan auto/manual behavior writes, then sends speed writes only for manual
fans. Auto/Max are distinct firmware behaviors, not aliases for speed 0/100. The desktop uses
service-provided `fan_name`/`fan_index` records and reported per-fan bounds, falling back to 0..100.
Exact fan count/names on the maintainer's Atlas remain unknown. Current measured fan speed is
displayed separately in RPM; no physical PWM/percentage calibration is established here.

Native `SUPPORT_FAN_CONTROL` construction (`14001ea00`..`14001fa00`) supplies literal bounds 0..100
for each supported fan. Fan-count discovery is decoded below; exact counts on the unit remain
unverified. Do not hardcode two Atlas fans from the desktop's two displayed rows.

The managed owner has no temperature-curve editor. The shared native service contains fan-table WMI
methods for other consumers, but their presence does not establish Atlas curve support.

## Fan discovery and EC health

Agent `140033790` initializes CPU/GPU/System counts to zero, reads `Model_Name_1st` from
`HKLM\SOFTWARE\OEM\AcerAgentService` and opens `<agent directory>\<Model_Name_1st>.ini`. Section
`FanSupport` has keys `CPU`, `GPU`, `System`, each defaulting to sentinel `NULL`. Present values are
parsed as integers. Only when all three keys are absent does it query a capability mask; a partial
INI suppresses the fallback and leaves missing groups at zero.

Topology mask bits are CPU 1, GPU 5/8 and System 3/7. CPU count is zero or one; the other counts are
the number of their two set bits. These are capability bits, not the setter's selection mask. Helper
`140035190` first tries EC HID health capabilities when `EC_HID_Support` is true, then falls back to
the legacy WMI getter. Discovery returns true even if both queries fail and counts remain zero; that
result alone does not establish zero physical fans.

HardwareService `14000dd10` translates service query 0 into EC subcommand 1 and query 1 plus index
into EC subcommand 2. `14001c190` uses the existing EC feature-report transport. Offsets exclude the
report ID, with little-endian fields:

| Query                  | Payload prefix         | Successful reply value     |
| ---------------------- | ---------------------- | -------------------------- |
| Health capability mask | `00 A0 08 00 01 00`    | uint16 at payload offset 5 |
| Indexed health reading | `00 A0 08 00 02 index` | uint16 at payload offset 7 |

The helper checks response command word 8 and result word `E000`, with the same locks and 20 ms
SetFeature-to-GetFeature delay. Its wrapper `14001b900` permits up to five read attempts, with
20/40/60/80 ms between attempts; selected errors control retryability. This getter behavior does not
authorize retrying writes. Agent `140035930` reads indices 6/9, but their metric names and units
remain unresolved; do not label them RPM, degrees or watts from the numeric indices alone.

[Native follow-up evidence](research/power-fan-agent-native.md) records the exact REA function
Evidence IDs and the correction to the earlier global-mode interpretation.

## Selection, recovery and remaining evidence

Agent initialization reads `HKLM\SOFTWARE\OEM\AcerAgentService\EC_HID_Support`, treating exactly 1
as true. This selects several observation paths; it is not an immutable model selector and does not
gate the fan setter path inspected above. Do not reuse a vendor registry flag as exact LibHandheld
detection.

Runtime implementation must retain power data-version/capability response handling and the separate
fan getter layouts. Preserve per-fan original behavior/speed, the full manual power profile and
original active mode in firmware-bound recovery, and distinguish a written profile from a selected
mode. Live fan topology, mixed-fan recovery and restoration sequencing need further review. Do not
change another application's profiles or services as part of acquisition.

Attended acceptance still needs the unit's exact EC collection and firmware, returned limits, fan
identities/bounds, RPM response, mode and power effects, restoration after stop/suspend and
vendor-software coexistence. No build or fixture can replace those observations.

## Implementation and on-device follow-up

The native implementation and dated results are in [PROVENANCE.md](PROVENANCE.md). Power uses the
firmware's declared PL1 bounds and PL2 maximum, with the shared paired-limit constraint. It does not
invent a preset wattage table. The wire data version is diagnostic information, not a substitute for
the captured machine's firmware identity.

The inspected V1.03 ACPI SSDT ACRPRDCT GGSI method reads system fan RPM at health indices 4 and 8;
the Agent's 140035850 and 1400358C0 getters use those indices unchanged. The EC feature transport
returns their uint16 values at payload offset 7. These observations establish the Atlas SYS/SYS2
readings, not a generic interpretation of every health index. Fan behavior readback remains unknown
on this unit even when WMI commands are accepted. Auto fallback is safe cooling policy, not proof of
exact original restoration.

The 2026-10-29 calibration in PROVENANCE.md established that a speed command is absolute only while behavior 3 is re-armed about every second; otherwise the firmware adds the command to its automatic level a few seconds after the behavior write. Speed commands below 10 stall the fans.
