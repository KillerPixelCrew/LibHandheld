# LibHandheld implementation coverage

Current source reconciliation: 2026-10-12. The immutable device inventories and pinned source
snapshots remain unchanged.

All **201 HC/HHD source records** are accounted for: 190 map to production paths and 11 broad
fallback records are policy exclusions. The library declares **112 exact runtime definitions**. The
former 11 vendor/CPU fallback ranges are excluded by the exact-identity policy. Definitions, aliases
and records are not a count of unique physical devices.

**Source implementation is separate from hardware validation.** Release library/application
compilation passed with zero warnings/errors. The parity regression batch covers native protocols,
input, lifecycle, presets and all embedded glyph profiles; exact results are recorded in the WSGM
parity audit. No new device hardware pass is claimed or required as an implementation gate.
Historical Claw/Ally evidence is retained.

HC has Windows priority. Every original selector and required feature declaration remains in the
JSON. `implemented-source` names concrete production paths. `not-model-owned` retains an optional
stock external-controller/sensor declaration without inventing onboard hardware. No outstanding
model-owned feature is currently identified by this reconciliation.

| Family     | Runtime definitions |
| ---------- | ------------------: |
| Ayaneo     |                  31 |
| Generic    |                  16 |
| Gpd        |                  20 |
| LegionGo   |                   7 |
| MsiClaw    |                   5 |
| OneXPlayer |                  25 |
| RogAlly    |                   4 |
| SteamDeck  |                   3 |
| Zotac      |                   1 |

HC 1.3.3.1 parity corrections include input-to-output motion transforms, canonical rear ordering,
current KUN/FLIP packets, XG fan reset, Zotac CMOS UMA, independent AMD fast PPT, processor
settings, native Valve haptics and distinct read-only measurements. All 112 definitions select one
of 106 authored glyph profiles; the host imports and parses the actual artwork. Source font
omissions use attributed Fluent/OFL equivalents with explicit provenance.

Native fan curves remain accessible beside host software curves, including when their settings
cannot be read. Steam and Overlay share descriptor controls and command/state owners. An unsafe
upstream zero-to-SET power query is not copied as a getter; accepted targets remain authoritative.

HC `SteamDeck` maps to LCD/Jupiter and OLED/Galileo. `SteamMachine` is the separate Fremont
definition with real native CPU controls and the signed Valve LED transport. HHD FLIP is an explicit
power-only 8–25 W profile; Minisforum V3 keeps its source-backed HPPAC/V3 aliases.

The three Aura records identify secondary USB modules, not new handhelds. Their exact VID/PID, HID
application and report shape select real codecs through `AsusAuraProtocol`, `AsusAuraTransport` and
`AsusAuraController`. `GenericAsusService` owns operations and lifecycle. `ProfiledDeviceEngine`
composes accessory-only state on non-ASUS models without replacing their device backend; ASUS
firmware models compose the modules directly.

`StandardControllerSource` selects the model's declared USB identifiers and requires an unambiguous
physical location before claiming an XInput slot. Profiles with companion USB identifiers also
require the controller and companion to share their built-in hub. HID fallback selection requires
one documented collection with its physical identity. No separate USB ancestry proof or native USB
query was executed in this reconciliation.

Every mapped source path exists, and the JSON has 201 unique target IDs. This source existence check
proves neither compilation nor hardware operation. Runtime readiness still depends on the actual
firmware, endpoint, sensor and native dependency.

Stock external-XInput and global serial-sensor declarations do not establish tablet/desktop onboard
hardware. Broad vendor/CPU fallbacks remain comparison records excluded from runtime selection;
unknown boards never inherit guessed model EC writes. Charge semantics remain model-specific: Lenovo
native 80% protection differs from other percentage ceilings. Persistent global firmware settings
remain distinct from per-game policy; missing readback and restart requirements remain visible.
