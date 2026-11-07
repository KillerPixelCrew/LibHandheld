# Intel IPF primitive and platform map

On the Acer Predator PA08-I51 tested on 2026-11-07, the installed IPF accepts a temporary GPU
cap-register change through its ordinary elevated IPC endpoint. CPU undervolting has no usable route
established on this configuration: undervolt protection is enabled, overclocking is disabled and
secured, and the XTU endpoint refuses this client. No voltage write was attempted.

This is research evidence, not a new LibHandheld capability or a hardware acceptance claim for
another model. The [existing transport description](intel-ipf.md) owns the client ABI and lifecycle.
The tested unit reported BIOS V1.04.

## Complete inventories for the installed build

| Inventory                                                 | Coverage                                                                                                                                                   |
| --------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Primitive IDs and permissions](intel-ipf-primitives.csv) | All 832 GET/SET names in the installed server's primitive-name dispatch table; eight explicit client-role allowlists; platform binding counts              |
| [Platform bindings](intel-ipf-bindings.csv)               | All 3,941 entries from the installed device-support database: domain, instance, operation, request/result types, ordered action IDs and dispatch selectors |
| [Device packages](intel-ipf-packages.csv)                 | All 35 packages, 46 domain declarations and extracted-package SHA-256 identities                                                                           |

The inventories describe shipped declarations, not 3,941 live calls. A package present in `dsp.dv`
does not mean its hardware is installed. A role permitting a primitive does not mean the selected
participant implements it. `installed_binding_count=0` means no binding in this database, not that
the primitive cannot be supplied by a different participant or plugin.

All binding primitive IDs resolve to the shipped server's names. Action IDs 89, 90 and 91 retain
`UNKNOWN_` names because the archived source lacks them; their numeric IDs and dispatch selectors
are preserved. The map does not claim complete semantics for these actions or undocumented mailbox
payloads. Raw action parameters and decompiled vendor code remain in ignored local evidence.

## Artifact and analysis identity

The installed package is
`C:\Windows\System32\DriverStore\FileRepository\ipf_cpu.inf_amd64_5fe37e024ff302b0`, IPF
`2.3.20303.5058`, IPC server SDK `1.0.23203.03`.

| File          | SHA-256                                                            |
| ------------- | ------------------------------------------------------------------ |
| `ipfsrv.dll`  | `576919a7876cb1af95b6e1199648943ace69ee91ec5a7cebb91d9af7372c033d` |
| `ipf_cpu.sys` | `6692e265ca8f906ddb31f9f80a1d81cfca40f799b16ffc608dea671ee637363b` |
| `ipfipc.dll`  | `cf2eafb2c71a35657f83e1b9e425edd1de52d222bfbfa637ff6255cb001e9113` |
| `dsp.dv`      | `86dc900633399b2cfcbf678f535f9887a3301c99492908df67a8cac88ee634bc` |

REA 6.8.0 ran locally through its MCP stdio interface and Rizin command interface. Native
decompilation used Ghidra 12.1.4, Node 24.22.1 and Microsoft OpenJDK 25. The downloaded Ghidra
archive matched the upstream release digest
`ddac49f903da9d5bac833e5cc79395098b9c33cfd3279be5f31bd00387d2d4db`. No permanent agent registration
was changed.

Intel's
[archived DPTF source](https://github.com/intel/dptf/tree/468699545442788999071d0a8ccb71b35332cdf1)
supplies historical structure definitions. It is older than the installed binaries and is not the
authority for the new primitive IDs or current permissions. For example, the installed elevated
allowlist permits `SET_PERF_PRESENT_CAPABILITY`, while that source's elevated list does not.

Local evidence is under `artifacts/ipf-map-20261107/` in the WSGM checkout. It includes REA
request/results, exported server Evidence, native pseudocode, primitive-name reconstruction,
validated database extraction, complete JSON maps, probe source/build logs and dated live logs. The
CSV exports contain protocol facts rather than vendor binaries or reconstructed source.

## How the map was recovered

1. REA collected string evidence from `ipf_uf.exe`, `ipfsrv.dll` and `ipf_cpu.sys`, then opened the
   server and CPU driver in separate Ghidra sessions.
2. Server function `0x18001f150` maps primitive IDs through a 1,321-slot jump table at
   `0x180020b7c`. Ghidra reports too many branches to reconstruct this switch. The mapping script
   follows each shipped jump-table entry and validates its `lea`/`ret` string-return stub instead.
   This recovers the 832 declared GET/SET names without executing the vendor function.
3. Server function `0x18001d190` initializes the role definitions. Its primitive allowlists are
   arrays of 32-bit permit/ID pairs terminated by a zero pair. REA read the elevated and XTU arrays;
   the offline decoder extracts the remaining arrays from the same hashed image.
4. `dsp.dv` is a version-2 `KEYS` DataVault. The parser verifies its embedded payload SHA-256,
   extracts its 35 EDP values, follows each EDP's FPC offset and decodes the domain/primitive/action
   hierarchy. Every primitive and domain must finish at its declared boundary. These Windows
   structures have padding absent from several older public declarations; the primitive header is 28
   bytes, the action header 44 bytes and the domain header 236 bytes in this build.
5. Selected, explicitly named GET primitives were called over a separate elevated IPC session. No
   discovery loop submitted SETs or arbitrary mailbox commands.

## Roles and endpoints

The installed marker directory advertises `public`, `elevated`, `dptf`, `dptfui`, `dptftcs`, `icst`,
`icstsvc` and `xtu`. Endpoint names use `pipe://ipfsrv.<role>`.

| Role     | Explicit permitted primitive IDs | Observed access                                                    |
| -------- | -------------------------------: | ------------------------------------------------------------------ |
| public   |                              130 | Inventoried; no new public-session test                            |
| elevated |                              163 | Connected and exercised as administrator                           |
| dptf     |                              381 | Inventoried; not connected                                         |
| dptfui   |                                0 | Empty primitive ACL; other interface operations have separate ACLs |
| dptftcs  |                               54 | Connection refused with 3108                                       |
| icst     |                               28 | Inventoried; not connected                                         |
| icstsvc  |                               31 | Inventoried; not connected                                         |
| xtu      |                               44 | Connection refused with 3108                                       |

3108 is `ESIF_E_WS_UNAUTHORIZED`; 3703 is `ESIF_E_SESSION_PERMISSION_DENIED` after an admitted
connection. Being an administrator does not grant every client role. The server also defines a root
role that permits all primitives, but no root endpoint was advertised or tried. No role
impersonation, ACL change, service restart or security-policy change was performed.

GetConfig, SetConfig, event operations, logging and SendCommand have separate permission tables; the
CSV describes primitive permissions only. An empty primitive ACL does not describe those other
operations. Their wire function pointers are in the existing ABI document.

The probe uses a distinct application name, `WSGMIPFResearch`, while WSGM remains running. Reusing
`WSGM` caused the server to disconnect after `AppGetName`; using the distinct name completed the
normal callbacks and enumeration. Participant and domain handles are session-specific, not saved
identifiers.

## CPU voltage and overclocking

For the live TCPU/PKG domain, the Panther Lake package binds these three getters to MSR `0x195`:

| Primitive                         |   ID | Bit | Live value |
| --------------------------------- | ---: | --: | ---------: |
| `GET_OVERCLOCKING_ENABLED_STATUS` | 1225 |   0 |          0 |
| `GET_UNDERVOLT_PROTECTION_STATUS` | 1032 |   1 |          1 |
| `GET_OVERCLOCKING_SECURED_STATUS` | 1226 |   2 |          1 |

The values were read through IPF, not a separate ring-0 helper. They establish this unit's reported
state, not the behavior of a future BIOS or different processor.

There is no named CPU voltage-offset SET in the 832-name inventory. The XTU allowlist includes
per-core voltage read 1088 and overclocking queries, but the installed Panther Lake package has no
binding for per-core voltage read 1088. Do not interpret the existence of its name as working
access.

There are lower-level overclocking leads, so absence of a named voltage setter is not proof of
impossibility. XTU is allowed OC BIOS mailbox command/data reads 1059/1061 and writes 1060/1062; the
Panther Lake package binds these to MMIO `0x5da4`/`0x5da0`. Elevated is denied all four.
`GET_OC_MBOX` 830 is XTU-permitted but occurs only in `mtl_proc.edp` in this database; its action
uses selector `OCMB` and MSR `0x150`. `SET_OC_MBOX` 831 has no explicit permit in these eight role
tables and no installed package binding. No raw mailbox sequence was dispatched.

`SET_UNDER_VOLTAGE_THRESHOLD` 519 and `GET/SET_UVTH` 671/670 are separate threshold/configuration
mechanisms. They are not a CPU voltage-offset API. FIVR/DLVR RF-profile frequency and
spread-spectrum controls likewise describe regulator interference tuning, not a CPU undervolt
setting.

Conclusion for this configuration: no working undervolting path was established. There is no
evidence supporting adding an undervolt control to LibHandheld or WSGM from this research.

## GPU frequency and cap paths

TCPU's live domains are PKG/D0, CPU/D1 and GFX/D2. The Panther Lake GFX package declares:

| Primitive                         |  ID | Data and backend                                   | Elevated access                           |
| --------------------------------- | --: | -------------------------------------------------- | ----------------------------------------- |
| `GET_PERF_SUPPORT_STATES`         | 137 | Binary table; kernel delegate `RPCP`               | Denied, measured 3703                     |
| `SET_PERF_PRESENT_CAPABILITY`     |  82 | UINT32 P-state table index; kernel delegate `RPCP` | Permitted statically; not written         |
| `GET_PERF_PRESENT_CAPABILITY`     | 987 | UINT32; MMIO `0x5994`, bits 7:0                    | Read successfully                         |
| `SET_PERF_PRESENT_CAPABILITY_SUR` | 304 | UINT32; MMIO `0x5994`, bits 7:0                    | Temporary write and restoration succeeded |
| `GET_PROC_RP_STATE_CAPABILITY`    | 277 | Instances 0/1/2; bytes of MMIO `0x5998`            | Read successfully: 132/0/6                |

REA traces the indexed path to `GfxIntfcSetPStateLimit` at `0x14004b524`. Interface version 1 uses
`0x14004b6e0`; version 2 uses `0x14004b940`, which obtains a frequency table from `0x140049a30` and
passes the selected entry to `0x14004b2c4`. The table builder obtains the graphics driver's
RP0/RPn/increment information and builds a descending list. Its table reader is `0x14007a4a0`. These
are static traces, not proof that this live graphics driver accepted an index.

An index for primitive 82 is not MHz and is not the register byte used by 304/987. The current
permission split permits an indexed setter but denies its frequency-table getter to elevated. Do not
manufacture an index-to-MHz mapping or use the RP capability bytes as that table.

The direct register path was tested once with the exact observed baseline and bounds:

| Stage                        | IPF result | Readback |
| ---------------------------- | ---------: | -------: |
| Original 987 read            |          0 |      255 |
| SET 304, raw 120             |          0 |      120 |
| Read after two seconds       |          0 |      120 |
| SET 304, restore raw 255     |          0 |      255 |
| New session after disconnect |          0 |      255 |

No write was retried. Restoration ran in `finally`. The independent final read also reported
undervolt protection still enabled. WSGM and IPF stayed running throughout.

This proves read/write access to the GPU cap register through IPF on this unit. It does not prove an
actual clock ceiling under load, MHz scaling, a minimum-clock control, GPU voltage control, reset
behavior, suspend/resume recovery or client arbitration. In particular, do not assume disconnect
releases primitive 304 merely because RAPL requests are released on disconnect. No application GPU
control was added.

The framework binary also contains IGCL frequency and GPU voltage-offset function names. Those names
alone do not establish an exposed IPF primitive or device support; the framework contains backends
for hardware other than this integrated GPU.

## Proof limits and next implementation boundary

The standalone probes compiled and ran; their builds reported style warnings. This research did not
change application code, deploy WSGM, build the solution, run application tests, connect to Steam
CEF or claim UI acceptance. Full primitive discovery is static; live probing was deliberately
limited to selected reads and the single reversible GPU cap test above.

A product GPU clock control needs a measured frequency mapping and effect under load, plus a defined
restoration/arbitration contract. Reusable GPU driver mechanics belong in LibGPUDriverInteract; IPF
participant and transport mechanics already live in LibHandheld. WSGM would own policy and both UI
projections. CPU undervolting remains unsupported by this evidence.
