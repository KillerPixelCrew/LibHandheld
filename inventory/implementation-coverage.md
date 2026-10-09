# LibHandheld implementation coverage

Current source reconciliation: 2026-10-09. The immutable device inventories and pinned source
snapshots remain unchanged.

All **201 HC/HHD source records** have their actual source implementations mapped. The library
declares **123 runtime definitions**, including **11 named fallback ranges**. Definitions, aliases
and records are not a count of unique physical devices.

**Source implementation is separate from validation.** Current-tree compilation is not yet
confirmed; automated tests remain deferred until the maintainer's manual pass; no new device
hardware pass is claimed. Historical Claw/Ally evidence is retained.

HC has Windows priority. Every original selector and required feature declaration remains in the
JSON. `implemented-source` names concrete production paths. `not-model-owned` retains an optional
stock external-controller/sensor declaration without inventing onboard hardware. No outstanding
model-owned feature is currently identified by this reconciliation.

| Family     | Runtime definitions |
| ---------- | ------------------: |
| Ayaneo     |                  31 |
| Generic    |                  27 |
| Gpd        |                  20 |
| LegionGo   |                   7 |
| MsiClaw    |                   5 |
| OneXPlayer |                  25 |
| RogAlly    |                   4 |
| SteamDeck  |                   3 |
| Zotac      |                   1 |

HC `SteamDeck` maps to LCD/Jupiter and OLED/Galileo. `SteamMachine` is the separate Fremont
definition with real native CPU controls and the signed Valve LED transport. HHD FLIP is an explicit
power-only 8–25 W profile; Minisforum V3 keeps its source-backed HPPAC/V3 aliases.

The three Aura records identify secondary USB modules, not new handhelds. Their exact VID/PID, HID
application and report shape select real codecs through `AsusAuraProtocol`, `AsusAuraTransport` and
`AsusAuraController`. `GenericAsusService` owns operations and lifecycle. `AsusAuraDeviceEngine`
composes accessory-only state on non-ASUS models without replacing their device backend; ASUS
firmware models compose the modules directly.

The physical controller ownership filter in `StandardControllerSource` and `UsbPortIdentity`
requires an unanchored XInput/HID device to occupy a proved internal USB port chain. Each ancestor
must be non-user-connectable, and current driver keys correlate the exact queried port to its child
devnode. A fixed leaf inside a removable external hub cannot qualify. Companion-anchored profiles
retain their established matching path. Unknown/truncated/ambiguous topology produces unavailable
input rather than capturing an external clone.

The USB ABI was checked against the installed Microsoft Windows SDK 10.0.26100.0 headers. Pure
parser/selector fixtures exist in `tests/Devices/UsbPortIdentityTests.cs`; they have not been
executed. No native USB query was run as part of this reconciliation.

Every mapped source path exists, and the JSON has 201 unique target IDs. This source existence check
proves neither compilation nor hardware operation. Runtime readiness still depends on the actual
firmware, endpoint, sensor and native dependency.

Stock external-XInput and global serial-sensor declarations do not establish tablet/desktop onboard
hardware. Known vendor ranges retain source-backed input/motion/OEM behavior; unknown boards never
inherit guessed model EC writes. Charge semantics remain model-specific: Lenovo native 80%
protection differs from other percentage ceilings. Persistent global firmware settings remain
distinct from per-game policy; missing readback and restart requirements remain visible.
