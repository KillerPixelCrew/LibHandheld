# AcerAgentService native power/fan translation

Offline bounded objdump/string analysis, 2026-10-27. Source
`drivers/AcerAgentServiceHH/swc/AcerAgentService.exe` in supplied Predator Sense package. SHA256
`12FB11542015E556DA3D50BD4BF0EB6260A62186042CDD141A9EA7505ECCF819`. Raw disassembly:
`power-managed/AcerAgentService-disassembly.txt`. No vendor executable run, service connected, or
hardware accessed. Addresses below are image VAs for this exact binary.

## Fan controls use WMI service commands

JSON FAN_CONTROL dispatch identified at VA140027540..14002769F: reads mode; mode2 calls custom
handler140035B40. Other modes build group mask/behavior and call140018180.

- Wrapper140018180 is positively named
  `namedpipe_hardware_service_function::WMISetGamingFanGroupBehavior` by adjacent diagnostic strings
  (VA140088730). Sends hardware-service command ID0x0D with eight-byte parameter through
  helper140059180. Return value is an error code (zero success).
- Wrapper1400185D0 is positively named `...::WMISetGamingFanGroupSpeed` (string VA1400888A0). Sends
  hardware-service command ID0x0F with eight-byte parameter.
- Custom handler140035B40..1400364F6 reads `fan_custom_auto`, `fan_custom_speed`, and matches
  `fan_name` values CPU, SYS, SYS2, GPU, GPU2 (assembly string compares visible
  near140035F1A..140036146). Managed service-provided names therefore matter, not only numeric
  fan_index. This pass does not prove fan_index has no other role.

Behavior group packing is:

`(packedTwoBitBehaviors << 16) | selectedFanMask`

CPU occupies maskbit0 and behavior bits0..1. SYS fan j occupies maskbit(j+1), behavior bits(2+2*j).
GPU fan j occupies maskbit(j+3), behavior bits(6+2*j). The dispatch routine uses enum mode+1 as
behavior: Auto mode0 -> behavior1, Max mode1 -> behavior2. Custom routine splits selected groups
into auto behavior1 and manual behavior3, sends separate behavior writes for each nonempty mask
(VA1400361B2..1400362ED).

Custom manual speed packing (VA14003633D..14003645A):

`(speed << 8) | selector`

CPU selector1, SYS fan j selector(j+2), GPU fan j selector(j+4). Speed is passed directly from
parsed integer, with no scaling visible between parse and wrapper call. Only entries with auto flag0
get speed writes. Thus service-side0..100 maps directly into this WMI payload field; physical
PWM/duty percentage interpretation still needs native WMI/firmware contract or hardware evidence.

This inspected FAN_CONTROL path does not branch on EC_HID_Support and does not call EC HID fan
setters. Do not assume power's HID transport also owns fan control.

## EC HID selection flag

Initialization near14001D529..14001D5A7 reads HKLM `SOFTWARE\OEM\AcerAgentService` value
`EC_HID_Support`; compares value exactly1 and stores bool at globalVA1400B3038. Registry read
failure defaults through initialized storage. This is a vendor cached support flag, not machine
identity.

Several telemetry helpers branch on this global, including140035930: false first uses old function
getter140017480 with selector encoding `(type<<8)|1`, true uses1400334F0. Type table6/9 is present
for the two indexed observations there. Full semantic names are not proven in this bounded trace. Do
not generalize that telemetry routing determines the FAN_CONTROL write path.

## Power forwarding

Wrapper140018A20 identified as `namedpipe_hardware_service_function::HIDSetOperationMode` from
stringVA140088A50; debug logs mode/PL1/PL2/error. Builds message command word0x45 (VA140018AFF),
serializes three input fields from struct offsets+4,+8,+0x14 (calls14001A100 at140018B20/30/40),
then sends through hardware service. Caller140036530 copies a28-byte structure and invokes this
wrapper, treating returned zero as success.

This establishes forwarding of HID operation-mode command0x45 and three fields, but this pass does
not independently assign each struct offset to mode versus PL1/PL2 or prove any unit conversion in
the earlier JSON-to-struct path. Managed callers separately prove integer watt units at their
service boundary. Main hardware-service analysis must confirm EC report packing and power field
units.

## Remaining gaps

Exact Atlas service-supplied fan names/indices/counts and current EC_HID_Support cannot be known
from these files alone. WMI class/method names from earlier HardwareService strings are
AcerGamingFunction/SetGamingFanBehavior/SetGamingFanSpeed, but the eight-byte parameter must be
traced in HardwareService to establish its exact gmInput handling. No fan limit/restore/command
result behavior is proven on hardware. A binary contract is evidence for implementation, not live
acceptance.

## HardwareService WMI handler followup

Inspected exact AcerHardwareService.exe identified in findings.md; raw source
`AcerHardwareService-disassembly.txt`.

- Service behavior setter handler nearVA14000B7xx calls WMI helper140021260 with packed uint64 input
  in RCX and output uint32 pointer in RDX (call14000B800). If helper returns false, handler
  substitutes0xFFFFFFFF; otherwise preserves returned32-bit gmOutput. It emits a four-byte service
  response. No unpacking or interpretation of the fan mask/behavior bits occurs in this handler.
- Fan speed setter helper is1400216A0 (method invocation block140021759..1400217EF); exact method
  `SetGamingFanSpeed` at stringVA14003A408. Behavior helper140021260 uses `SetGamingFanBehavior`
  atVA14003A3B0. Class `AcerGamingFunction` atVA14003A330; instance query
  `SELECT * FROM AcerGamingFunction` atVA14003A2C0. Namespace initializer string `ROOT\WMI`
  atVA14003A130.
- Both setters pass property name `gmInput` (VA14003A268) and read `gmOutput` (VA14003A278). Input
  VARIANT starts as VT_UI8(0x15) containing the unchanged64-bit packed value, then converts to
  VT_BSTR(8) for WMI. WMI's uint64 transport is therefore represented as a numeric string in COM.
  Output converts to VT_UI4(0x13) and writes its32-bit value to caller. A .NET implementation should
  use semantic ulong input/uint output per WMI declaration; do not pass a signed32-bit mask or
  floating point value.
- Getter pair uses corresponding `GetGamingFanBehavior` (stringVA14003A380) and `GetGamingFanSpeed`
  (VA14003A3E0),32-bit input and64-bit output. Observed getter behavior helper around1400210xx
  converts returned VARIANT to VT_UI8(0x15) before copying8bytes. This is separate from
  setter's32-bit firmware status.
- Both setters sleep30ms after a successful invocation/output conversion (Sleep importVA14002F7B0).
  This is post-operation pacing, not a retry.
- COM helper14001EC50 returning zero is failure. The code compares VariantChangeType's HRESULT
  specifically to1 and treats that case as failure; it does not use the ordinary negative-HRESULT
  check in these visible wrappers. Preserve a clear distinction between transport failure, returned
  firmware status, and success; do not copy that vendor HRESULT quirk.

Agent wrappers return the numeric service status, and upper fan control tests its low byte for zero
success. HardwareService does not prove firmware semantics for every nonzero code. No write was
performed to establish status acceptance or physical fan scaling.

## Final fan getter/restoration followup

Bounded source pass, no further tooling or live operations. Agent getter140033C40..1400350B2 calls
behavior wrapper1400183A0 with the populated fan mask (CPUbit0; SYSj bitj+1; GPUj bitj+3). Rejects
returned low status byte!=0 at140033D39.

Raw64-bit getter output includes low8-bit status. Behavior fields are CPU `(reply>>8)&3`, SYSj
`(reply>>(10+2*j))&3` (14003459F), GPUj `(reply>>(14+2*j))&3` (140034B8B). This is different from
write packing: setter behavior fields begin atbit16, getter fields begin atbit8.

For per-fan auto serialization it checks behavior3 (manual -> auto0) versus behavior1 (auto ->
auto1); other behavior values follow a separate empty/default branch, so do not equate every
nonmanual value with known auto state. It separately remembers whether any behavior3 exists for
global mode fallback.

Speed getter wrapper1400187F0 is called with CPUselector1 (EDI set1 at140033F16, call140034199),
SYSj selectorj+2 (14003477E), GPUj selectorj+4 (140034D5x). It rejects low status byte!=0; otherwise
serializes `(reply>>8)&0xFF` as fan_custom_speed (1400341BF,1400347A4,140034D7E). No
multiplication/division is applied to the extracted speed.

Global UI mode is not a reliable hardware-only observation: near140034FEE..140035053 the getter
prefers registry `HKLM\SOFTWARE\OEM\AcerAgentService\FanControl` value `CurrentFanMode`. If that
registry read fails, any observed manual behavior3 causes Custom2; otherwise another accumulated
flag chooses Max1/Auto0 (14003505F..140035076). This pass did not fully prove the accumulated flag's
mixed-fan semantics. Restoration should retain exact per-fan behavior/speed, not rely only on that
cached UI enum.

SUPPORT_FAN_CONTROL construction near14001EAxx..14001FAxx writes fan_custom_max_speed100 and
min_speed0 as literal JSON values (first pair14001EEDF and14001EF76; repeated for later fan groups).
Names and indices are built from the fan counts. Count discovery helper140033790 combines
configuration/INI and WMI fallback per neighboring diagnostic strings
(`INI FAN CPU:%d GPU: %d SYS: %d`, `WMI FAN CPU:%d GPU: %d SYS: %d`). Exact precedence, config keys
and WMI count bit extraction were not resolved in this bounded pass. Do not advertise a fixed
two-fan Atlas recipe until exact runtime inventory/source mapping is confirmed.

Remaining recipe gaps: exact per-unit fan name/index/count evidence; acceptable restored speed under
firmware max mode; mixed-fan global mode representation; support-count decode. No automatically safe
restoration sequence or hardware acceptance is established solely by these source findings.
