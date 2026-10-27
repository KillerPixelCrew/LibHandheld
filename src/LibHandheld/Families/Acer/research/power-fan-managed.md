# Managed power and fan findings

Offline inspection of Predator Sense 1.0.1563.0 desktop assembly, 2026-10-27. No vendor executable,
service connection or hardware operation. Extracted classes are under `power-managed/`; original
binary/provenance identities are in `findings.md`.

## Power

`power-managed/OperatingMode.cs`: Turbo=0, Balance=1, Quiet=2, Manual=3. `SystemStateModel.cs`
MapOperatingModeFromRaw confirms the same mapping.

`AcerAgentService.cs` SetOperatingModeAsync sends service function `OPERATING_MODE` with parameter
`{mode:<enum integer>}`. `SystemStateModel.cs` SetManualOperatingModeAsync sends
`{mode:3,PL1:<integer>,PL2:<integer>}`. These numbers are **watts** at the desktop-to-agent
boundary, proven by PerformancePage CommitManualModeAsync and ApplyPlSliderBounds logging them with
W suffix.

Fallbacks in SystemStateModel: PL1 min10/max40/current35 W; PL2 min15/max65/current65 W. They are
application defaults, not proven Atlas hardware limits. `SUPPORT_OPERATING_MODE` supplies `PL1_Min`,
`PL1_Max`, `PL2_Max` overrides. `OPERATING_MODE` readback also includes `mode`, `PL1`, `PL2` and
those bounds. PL2_Min is not read from the capability response in this code.

PerformancePage rounds sliders to integer watts. PL1>=PL2 shows invalid-value dialog and adjusts PL2
to PL1+5. SystemStateModel clamps PL1 to its bounds; its PL2 clamp uses the existing `Pl1` property
rather than newly passed local pl1, a vendor quirk not to reproduce. PL2 slider minimum is
hardcoded15 when ShowPL2 debug setting is true; otherwise Pl2Min. No preset-mode wattage table is
defined in these extracted callers.

Vendor default policy: if user has not changed operating mode, plugged-in selects Turbo, battery
selects Balance (`ApplyAutoOperatingModeForPowerSource`). This is vendor policy, not firmware
protocol or required LibHandheld behavior.

## Fan

`power-managed/AcerFanMode.cs`: Auto=0, Max=1, Custom=2.

`AcerFanControlRequest.cs` serializes:

```json
{
  "mode": 2,
  "custom_fan_data": [
    {
      "fan_index": 0,
      "fan_name": "<service supplied>",
      "fan_custom_auto": 0,
      "fan_custom_speed": 50
    }
  ]
}
```

Fan index/name must come from the service result, not the illustrative0. Nullable auto/speed fields
are omitted when unset. Empty custom data omits that array. Service function name is `FAN_CONTROL`.

`SystemStateModel.cs` SetFanControlAsync(mode,Fan1Auto,Fan1Speed,Fan2Auto,Fan2Speed) sets per-fan
auto1/manual0 and integer speed only when mode Custom; forwards both existing service entries. UI
assumes two fans and preserves service supplied indices/names. Max and Auto remain enum selections,
not manual speed100/0.

`AcerSupportedFanControlInfo.cs` decodes capability entries from `SUPPORT_FAN_CONTROL`: `fan_name`,
`fan_index`, `fan_custom_min_speed`, `fan_custom_max_speed`. Bounds default0..100 and are overridden
from first two entries. FanControlPage sets slider min/max from these values and casts selected
values to integers. Current status `FAN_CONTROL` contains `mode` and `custom_fan_data` with those
same index/name/auto/speed keys.

Current rotation displayed separately from `lastData.SystemFanSpeeds[0/1]`, explicitly suffixed RPM.
Manual speed0..100 is therefore a normalized control range; **percent/PWM units are plausible but
not proven by these callers**. No temperature points, curve editor, or temperature-to-duty table
exists in extracted FanControlPage/SystemStateModel/request objects. Vendor UI offers per-fan auto
or fixed custom speed, not a user-authored curve.

## Service boundary and Atlas path

`AcerAgentService.cs` uses TCP127.0.0.1:46933. Request payload for writes is
`{Function:"OPERATING_MODE"|"FAN_CONTROL",Parameter:{...}}`; GetUpdatedData passes Function and
optional Parameter. `AcerAgentPacketId.cs`: initialization0, monitor10, updated-data20,
connected-device30, lighting-capability40, set-device-data100, notification120. This is a
proprietary authenticated service protocol; no connection was attempted.

Desktop chooses no Atlas-specific WMI/HID transport in these classes. It calls the same service
functions. Selection between EC HID and AcerGamingFunction WMI occurs in native
AcerAgentService/AcerHardwareService and must be established there. These managed facts prove
semantic parameter values and UI units, not native report packing, byte scaling, HID collection
identity, fan selector bits or firmware capabilities on the maintainer's unit.

## Exact review pointers

- `power-managed/SystemStateModel.cs`: QueryCapabilityAsync, ApplyOperatingModeInfo,
  SetManualOperatingModeAsync, MapOperatingModeFromRaw, RefreshFanControlAsync, SetFanControlAsync.
- `power-managed/PerformancePage.cs`: CommitManualModeAsync, ApplyPlSliderBounds.
- `power-managed/FanControlPage.cs`: UpdateModePanel, CommitFanSettingsAsync and slider bound
  assignments (method names/source around lines200..280).
- `power-managed/AcerAgentService.cs`: GetOperatingModeAsync, GetFanControlAsync,
  SetOperatingModeAsync, SetFanControlAsync, SetDeviceDataAsync.
- `power-managed/AcerFanControlRequest.cs`, `AcerSupportedFanControlInfo.cs`,
  `AcerFanControlInfo.cs`, `OperatingMode.cs`, `AcerFanMode.cs`.

No hardware acceptance, write safety, preset power table or percent fan scaling established by this
offline pass.
