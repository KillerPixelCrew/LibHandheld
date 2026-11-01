# Intel IPF power-limit interface

LibHandheld can ask Intel's Innovation Platform Framework (IPF, the successor of Dynamic Tuning
Technology) for the CPU's RAPL power limits instead of writing MSR 0x610 or the MCHBAR mirror
itself. IPF applies the request through its own Intel-signed driver, so no driver blocklist or
Memory Integrity change is needed. This page documents the interface as LibHandheld uses it. It is
not a public Intel API: Intel publishes no SDK for it. Everything here comes from the IPF 2.3
binaries on an Acer Predator Atlas 8 (PA08-I51), reversed with Rizin, cross-checked against Intel's
archived Apache-2.0 DPTF/IPF sources (github.com/intel/dptf, archived 2026-08), and verified on the
device on 2026-11-01. The implementation is `Internal/Transports/IntelIpfTransport.cs`; the path
choice around it is `Internal/Devices/IntelPowerPaths.cs`.

## Components on the device

| Component        | Location                                                              | Role                                  |
| ---------------- | --------------------------------------------------------------------- | ------------------------------------- |
| `ipfsvc` service | `ipf_uf.exe` in the `ipf_cpu.inf_*` DriverStore folder                | IPF framework, policies, IPC server   |
| `ipf_cpu` driver | IPF Processor Participant, PCI `VEN_8086&DEV_B01D` on the Atlas       | Writes MCHBAR 0x59A0 and MSRs for IPF |
| `ipfipc.dll`     | Same DriverStore folder                                               | IPC client library (used by WSGM)     |
| `ipfcore.dll`    | Same DriverStore folder                                               | Core client library (not used, below) |
| Socket markers   | `C:\Windows\ServiceProfiles\LocalService\AppData\Local\Intel\ipfsrv\` | One file per server address           |

Version observed: IPF 2.3.20303.5058, server SDK `1.0.23203.03`. The marker folder lists the active
server addresses: `ipfsrv.public`, `ipfsrv.elevated`, and per-client names such as `ipfsrv.dptf`,
`ipfsrv.xtu` (Intel XTU is a client of the same interface).

Prerequisites: `ipfsvc` must be running, and the IPF processor participant device must be enabled.
Disabling that device (for example with `Disable-PnpDevice`) takes effect at the next boot, stops
`ipf_cpu`, and hides the device interface the client libraries look for; every later step then
fails. LibHandheld only offers the IPF path when `ipfipc.dll` exists and `ipfsvc` is installed.

## Transport

IPF's IPC is a WebSocket over a local socket that `ipfipc.dll` addresses as a "logical named pipe"
(it uses WS2_32). Server addresses, from `ipf_core_api.h`:

| Address                  | Access                                                              |
| ------------------------ | ------------------------------------------------------------------- |
| `pipe://ipfsrv.public`   | Public role                                                         |
| `pipe://ipfsrv.elevated` | Elevated role; the caller must be an administrator (`S-1-5-32-544`) |
| `ws://127.0.0.1:18086`   | Public role over TCP, localhost only                                |

LibHandheld connects to `pipe://ipfsrv.elevated`. The elevated role may set RAPL limits but may not
clear them (below).

## Client modes

Intel's own loader (`ipf_apploader.c`) has two modes:

- Core mode, `ipfcore.dll` (`GetIpfInterface`): `IpfCore_Init`, `SessionCreate`, `SessionConnect`,
  then primitives through `IpfCore_SessionExecute`, which parses an undocumented command string
  (`IpfCore_ParseAndExecuteCommand`). Not used.
- IPC mode, `ipfipc.dll` (`GetIpcInterface`): the client registers an ESIF application interface and
  the server calls it back, handing over a primitive function. Every structure is in the public
  headers. This is what LibHandheld uses.

## Structures

All handles (`esif_handle_t`) are 64-bit. Error codes (`eEsifError`) and enums are 32-bit, `ESIF_OK`
is 0. `ESIF_CALLCONV` is empty on x64, so every function uses the normal x64 calling convention.
Strings are ANSI and NUL-terminated.

`EsifIfaceHdr` (8 bytes): `UInt32 fIfaceType; UInt16 fIfaceVersion; UInt16 fIfaceSize;`

`IpcInterface` (`ipf_ipc_iface.h`, packed, 132 bytes, type `eIfaceTypeIpfIpc` = 8, version 1). The
client fills the header and the client SDK version; `GetIpcInterface` fills the rest. The DLL also
accepts version 2 with size 140, which adds a ninth pointer.

| Offset | Field                                                                 |
| -----: | --------------------------------------------------------------------- |
|      0 | `EsifIfaceHdr hdr`                                                    |
|      8 | `char Ipc_Version[20]` (out)                                          |
|     28 | `char Ipc_SdkVersion[20]` (out)                                       |
|     48 | `char Ipc_ClientSdkVersion[20]` (in)                                  |
|     68 | `Ipc_Init()`                                                          |
|     76 | `Ipc_Exit()`                                                          |
|     84 | `Ipc_SetAppIface(const AppInterfaceSet*)`                             |
|     92 | `IpcSession_Create(const IpfSessionInfo*)` returns the session handle |
|    100 | `IpcSession_Destroy(session)`                                         |
|    108 | `IpcSession_Connect(session)`                                         |
|    116 | `IpcSession_Disconnect(session)`                                      |
|    124 | `IpcSession_WaitForStop(session)`                                     |

Client SDK version: the server compares it with its own (`ipf_sdk_version_check.h`). An empty string
parses as 0.0.0 and fails with `ESIF_E_UNSUPPORTED_CLIENT_SDK` (1012). Any `1.0.x` not newer than
the server passes; LibHandheld sends Intel's public `IPF_SDK_VERSION`, `1.0.11402`.

`IpfSessionInfo` v1 (`ipf_core_api.h`, packed, 1456 bytes):

| Offset | Field                       |
| -----: | --------------------------- |
|      0 | `UInt32 revision` = 1       |
|      4 | `char appName[64]`          |
|     68 | `char appVersion[20]`       |
|     88 | `char appDescription[64]`   |
|    152 | `char appIntro[1024]`       |
|   1176 | `char serverAddr[260]`      |
|   1436 | `char sdkVersion[20]` (out) |

`AppInterfaceSet` (`esif_sdk_iface_app.h`, packed, 192 bytes, type `eIfaceTypeApplication` = 0,
version `APP_INTERFACE_VERSION` = 4, which means 64-bit handles). Only the set has a header; the two
interfaces inside it do not. The client fills the header and the 15 `AppInterface` pointers; the
server's side supplies `EsifInterface` when it calls `AppCreate`.

| Offset | Field                                                                                                                                           |
| -----: | ----------------------------------------------------------------------------------------------------------------------------------------------- |
|      0 | `EsifIfaceHdr hdr`                                                                                                                              |
|      8 | `AppCreate(AppInterfaceSet*, esifHandle, esif_handle_t* appHandle, AppData*, eAppState)`                                                        |
|     16 | `AppDestroy(appHandle)`                                                                                                                         |
|     24 | `AppSuspend(appHandle)`                                                                                                                         |
|     32 | `AppResume(appHandle)`                                                                                                                          |
|     40 | `AppGetVersion(EsifData* out)`                                                                                                                  |
|     48 | `AppCommand(appHandle, UInt32 argc, EsifData* argv, EsifData* response)`                                                                        |
|     56 | `AppGetIntro(appHandle, EsifData* out)`                                                                                                         |
|     64 | `AppGetDescription(EsifData* out)`                                                                                                              |
|     72 | `AppGetName(EsifData* out)`                                                                                                                     |
|     80 | `AppGetStatus(appHandle, eAppStatusCommand, UInt32 in, EsifData* out)`                                                                          |
|     88 | `AppParticipantCreate(appHandle, participantHandle, AppParticipantData*, eParticipantState)`                                                    |
|     96 | `AppParticipantDestroy(appHandle, participantHandle)`                                                                                           |
|    104 | `AppDomainCreate(appHandle, participantHandle, domainHandle, AppDomainData*, eDomainState)`                                                     |
|    112 | `AppDomainDestroy(appHandle, participantHandle, domainHandle)`                                                                                  |
|    120 | `AppEvent(appHandle, participantHandle, domainHandle, EsifData* data, EsifData* guid)`                                                          |
|    128 | `EsifInterface`: `GetConfig`, `SetConfig`, `Primitive`, `WriteLog`, `RegisterEvent`, `UnregisterEvent`, `SendEvent`, `SendCommand` (8 pointers) |

`EsifData` (`esif_sdk_data.h`) is declared under `#pragma pack(1)`, so it is 20 bytes, not 24:

| Offset | Field             |
| -----: | ----------------- |
|      0 | `UInt32 type`     |
|      4 | `void* buf_ptr`   |
|     12 | `UInt32 buf_len`  |
|     16 | `UInt32 data_len` |

Reading the pointer at offset 8 instead of 4 writes through a garbage address in the first string
callback.

`AppParticipantData` and `AppDomainData` are packed and start with `UInt8 fVersion` and three
reserved bytes. The participant name is the third `EsifData` (`fDriverType`, `fDeviceType`,
`fName`), at offset 44; the domain name is the first, at offset 4.

Data types used: `ESIF_DATA_UINT32` = 3, `ESIF_DATA_STRING` = 8, `ESIF_DATA_POWER` = 26
(milliwatts). String callbacks copy into the caller's buffer, set `data_len` including the NUL and
set the type to `ESIF_DATA_STRING`.

## Call sequence

1. `LoadLibrary` the DriverStore `ipfipc.dll` by full path and call `GetIpcInterface`.
2. `Ipc_Init`.
3. `Ipc_SetAppIface` with the filled `AppInterfaceSet`. Keep the set and every callback alive for
   the session's lifetime; the server calls them from its own thread.
4. `IpcSession_Create` with `serverAddr = "pipe://ipfsrv.elevated"`.
5. `IpcSession_Connect` returns as soon as the connection is up.
6. Run `IpcSession_WaitForStop` on a dedicated thread. It is the message pump that dispatches the
   server's calls, and it returns when the session closes. Without it, no callback ever arrives and
   `IpcSession_Disconnect` blocks.
7. Within about 0.1 s the server calls `AppGetName`, `AppGetDescription`, `AppGetVersion`, then
   `AppCreate`, `AppGetIntro`, then `AppParticipantCreate`/`AppDomainCreate` for every participant,
   then one `AppCommand` with two arguments. In `AppCreate`, save `esifHandle`, return an app handle
   and take `EsifInterface.Primitive` from the set the server passes.
8. The CPU participant is named `TCPU` (`ESIF_PARTICIPANT_CPU_NAME`). Its domains on the Atlas were
   `PKG`, `CPU` and `GFX`; the first, `PKG`, is D0, the package domain RAPL limits belong to.
9. To release: `IpcSession_Disconnect`, join the pump, `IpcSession_Destroy`, `Ipc_Exit`. The server
   calls `AppDestroy` during the disconnect.

## Primitives

`Primitive(esifHandle, participantHandle, domainHandle, EsifData* request, EsifData* response, ePrimitiveType primitive, UInt8 instance)`,
with the TCPU participant and its PKG domain. For RAPL limits the instance selects the limit, as
DPTF's `DomainPowerControl` does: 0 = PL1, 1 = PL2, 3 = PL4.

| Primitive                          |  ID | Data                             | Notes                                                                    |
| ---------------------------------- | --: | -------------------------------- | ------------------------------------------------------------------------ |
| `GET_RAPL_POWER_LIMIT`             |  38 | response `POWER` (mW)            | Read back 45000/65000 on the Atlas                                       |
| `SET_RAPL_POWER_LIMIT`             | 130 | request `POWER` (mW)             | LibHandheld writes PL2, then PL1                                         |
| `SET_RAPL_POWER_LIMIT_ENABLE`      | 222 | request `UINT32` 1               | Sent for PL1 and PL2 before the limits                                   |
| `SET_RAPL_POWER_LIMIT_TIME_WINDOW` | 340 | request `UINT32`                 | Not used                                                                 |
| `SET_RAPL_POWER_LIMIT_CLEAR`       | 881 | request `UINT32` 0, instance 255 | Refused for the elevated role: `ESIF_E_SESSION_PERMISSION_DENIED` (3703) |

IPF arbitrates these primitives across its clients (`esif_uf_arbmgr.c`): `SET_RAPL_POWER_LIMIT`
instances 0, 1 and 3 resolve to the lowest request, `SET_RAPL_POWER_LIMIT_ENABLE` to the highest. A
client's value therefore applies only while it is the lowest, including IPF's own policy request.
Closing the session withdraws the client's requests and IPF recomputes; that is the only release
available to the elevated role.

Measured on the Atlas: a 30/50 W request moved MCHBAR 0x59A0 from 45/65 to 30/50 W while MSR 0x610
stayed 45/65 W; after disconnect IPF chose 45/86 W. With 45/45 W requested, MCHBAR held 45/45 W for
four minutes of OCCT at 99-100 C, while the EC firmware path had been lowered to 35 W in that
situation. See the Atlas `PROVENANCE.md` for the full record.

## Errors seen while bringing it up

| Code         | Name                               | Cause                                                                                                       |
| ------------ | ---------------------------------- | ----------------------------------------------------------------------------------------------------------- |
| 1002 (0x3EA) | `ESIF_E_NOT_SUPPORTED`             | `AppInterfaceSet` header with the wrong version or size, or mixing core mode with `SetAppIface`             |
| 1012 (0x3F4) | `ESIF_E_UNSUPPORTED_CLIENT_SDK`    | Empty or non-1.0 client SDK version                                                                         |
| 1800 (0x708) | `ESIF_E_NO_MEMORY`                 | `ipfcore` `IpfCore_Init` when the processor participant device was disabled (its `InstallDir` lookup fails) |
| 3401 (0xD49) | `ESIF_E_IFACE_NOT_SUPPORTED`       | Same cause, inside `IpcApiObj_LoadApi`                                                                      |
| 3703 (0xE77) | `ESIF_E_SESSION_PERMISSION_DENIED` | `SET_RAPL_POWER_LIMIT_CLEAR` from the elevated role                                                         |

## Debugging

Both client DLLs trace through `OutputDebugString` with a level variable at the start of `.data`
(`ipfcore.dll` +0xB5000, `ipfipc.dll` +0xA2008, IPF 2.3 builds; default 1). Raising it to 5 in a
test process and reading the DBWIN buffer shows each lifecycle step and its exit code. Do not do
this in WSGM: at level 5 every IPF thread blocks in `OutputDebugString` until a debugger reads it.

The `ipf_uf.exe` shell is not usable for this: in the release build `shell` and `client -c` print
nothing ("IPF Shell Disabled"). The shell is gated by bit 2 (`ShellAccessControl`) of the BIOS DCFG
mask, or its output is compiled out.

Standalone probes used to establish all of the above are kept locally in `artifacts/ipf-re`
(`ipfprobe` runs the whole IPC sequence read-only, or writes with `IPF_WRITE=1`).
