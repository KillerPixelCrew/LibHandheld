# Remaining Windows devices and source-backed fallback ranges

Primary source: HandheldCompanion `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`.

- `Devices/IDevice.cs` and `Devices/Ayn/*`: four exact Loki model / CPU selectors, inherited fan
  control and RGB ACPI EC command interface, LCC keyboard chord, Guide mouse chord, and individual
  5–15 / 5–28 W bounds.
- `Resources/Devices/AynLoki.json`: separate gyro and accelerometer transforms. HC's old fan-reading
  code reads direct I/O ports and marks the result unverified; this implementation reads the actual
  ACPI EC duty register and never presents those unrelated ports as fan RPM.
- `Devices/Minisforum/MinisforumV3.cs` and resource JSON:
  `SHENZHEN MEIGAO ELECTRONIC EQUIPMENT CO.,LTD` / `HPPAC`, 3–28 W, real Windows sensor coordinate
  mapping. No built-in controller or firmware fan register is invented for this tablet.
- `Devices/ASUS/ASUS.cs`, `AsusACPI.cs`, `XGM.cs`: ASUS vendor fallback, ATKACPI closed device IDs,
  charge/performance controls, fan-curve format and XG Mobile ACPI / 300-byte HID dock commands.
  These are firmware mechanics, independent of LibGPUDriverInteract.
- `Devices/DefaultDevice.cs` and `IDevice.cs`: final unmatched fallback, inherited 10–25 W envelope.
  CPU-only definitions have `HasController=false`; their driver acquisition checks the actual native
  AMD SMU or Intel RAPL/MMIO implementation. Detection never performs hardware writes.

Cross-check / HHD-only source: HHD `a87fb308f9f14d4a78ef3cb0b8515827459f8466`.

- `src/hhd/device/generic/{const.py,base.py,__init__.py}`: Zeenix Pro, Win600, Pocket Go;
  system-product identity; actual Xbox or TECNO VID/PID; TECNO report `74` uses MSB-numbered bits
  `(5<<3)+7`, `(5<<3)+6`, `(7<<3)+2`; AYN/TecToy/AYANEO range selectors and sensor transforms.
- `src/hhd/device/orange_pi/*`: NEO-01 and vendor range; actual controller IDs and F15–F18 OEM keys;
  sensor transforms.
- `src/hhd/device/gpd/win/*`: GPD vendor range uses the actual WinControls collection and controller
  companion USB topology, documented touch digitizers and physical rear keyboard edges.
- `src/hhd/device/oxp/{const.py,__init__.py,base.py}`: ONEXPLAYER/AOKZOE product substring ranges.
  Their default protocol is absent / explicitly `none`, so HHD does not provide generic vendor
  RGB/backbutton writes. The Windows fallback keeps the actual standard controller, motion and
  keyboard sources; it never applies a known model's EC offsets to an unknown board.
- `src/adjustor/drivers/unified/const.py`: GZ302 board power profile, and G1617-02-L / G1622-01[-L]
  / G1628-04[-L] product power selectors. These additional GPD records are CPU-power-only
  definitions; no HID features are inferred from their marketing names.

HC exact selectors precede HHD aliases and fallbacks in the registry. Fallbacks are named ranges,
not claims of unique exact-device support. Every runtime profile binds to the current supplied
manufacturer, product and CPU identity before any native resource is acquired. Hardware readiness
and observations come from the actual transport; unsupported native CPU codenames and absent sensors
remain unavailable.

Loki fan originals and ASUS temporary mode / curves are journaled with the BIOS/EC identity. Read
failures do not prohibit a proved write, but missing originals or readback remain unverified at
shutdown. Charge ceilings are percentages and global persistent state, not per-game transient
policy. The common lifecycle reacquires motion/OEM/controller services on session unlock and resume;
there is no hidden write-on-unlock workaround.

No new hardware acceptance has been performed for these implementations.

## Aura USB modules

[HHD Aura at a87fb30](https://github.com/hhd-dev/hhd/blob/a87fb308f9f14d4a78ef3cb0b8515827459f8466/src/hhd/device/aura/__init__.py)
defines VID `0B05`, PIDs `18C6` (lightbar), `1A30` (keyboard), and `1BC1` (2025 XG Mobile).
`AsusAuraTransport` requires those actual USB collections, an approved application
`FF31:0076/0079/0080` or `FF00:0020`, matching feature size, and the expected feature-report
response. Multiple top-level collections of one physical module collapse to one logical zone;
physical USB location gives stable independent instances when more than one module is attached.

`AsusAuraProtocol` independently encodes the protocol facts from that source: normal modules use
64-byte `5D` Aura reports (brightness uses `5A`), while PID `1BC1` uses 300-byte `5E` reports
throughout. The first explicit command performs the ASUS handshake and, for XG Mobile, `E4 02`,
`C5 50`, and `BD 00 01` initialization. Modes use `B3`, followed by `B5/B4` on normal modules or
`B4/B5` on XG. Brightness maps to four native levels. Lightbar supports cycle/direction and strobe;
keyboard omits cycle; XG omits cycle and strobe. Normal-module boot/awake/sleep/shutdown flags use
the documented `BD` masks; XG power uses `C5` instead.

`GenericAsusService` composes the modules alongside its existing ATKACPI controls, with separate
instance IDs and existing semantic lighting roles. Its `firmwareControls=false` mode owns only
actual Aura USB endpoints, allowing an accessory on another manufacturer's host without advertising
ASUS laptop firmware controls. Endpoint topology changes increment the service descriptor generation
for the shared runtime to republish; suspend/detach closes handles and resume discovers fresh ones.

Discovery and resume send no lighting configuration. An uncertain write closes that module and is
latched across rediscovery/resume rather than automatically repeated. Successful explicit writes
publish observed state; no Aura readback of colors or power settings is invented. Before any write,
those settings remain unknown. Normal-module power masks use HHD's explicit preference defaults for
fields not yet authored; these are desired settings, not observed originals. Persistent settings
stay on the device at shutdown. Packet/lifecycle test sources use injected endpoints and do not
establish hardware acceptance.
