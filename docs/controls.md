# Controls

Controls returns a DeviceControlSnapshot containing descriptors and accepted/observed state.
Descriptors identify semantic role, capability and instance IDs, value kind, read/write support,
bounds, choices, units, persistence, profile scope and apply timing. Use current availability;
unknown is not zero. SupportsRead and SupportsWrite are independent.

| Area                  | Semantic operations                                                                                                         |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------- |
| Power                 | SetTdpAsync, SetPowerLimitsAsync, SetScenarioModeAsync                                                                      |
| Fan                   | SetFanModeAsync, SetFanDutyAsync, SetFanTargetRpmAsync, SetFanCurveAsync                                                    |
| Charge                | SetChargeLimitAsync, SetChargeProtectionAsync, SetChargeProtectionModeAsync, SetChargeBypassAsync, SetChargeBypassModeAsync |
| Lighting              | SetLightingPowerAsync, SetLightingBrightnessAsync, SetLightingColorAsync, SetLightingEffectAsync, SetLightingSpeedAsync     |
| Other native controls | SetControlAsync with a semantic CapabilityRole and typed CapabilityValue                                                    |

Power uses watts, duty/brightness/charge use percentages, RPM uses revolutions per minute and curves
use Celsius inputs with descriptor-defined outputs. RGB values are integers. Mode/effect values and
legal instances come from descriptors. Ambiguous fans or zones require explicit selection.

SetTdpAsync sets every declared power limit to one target. SetPowerLimitsAsync supplies independent
sustained, slow, fast and peak targets. Omitted limits remain unchanged; paired native limits must
be consistent. Validation checks the whole request before its first write.

Native fan curves are firmware operations; software curves and AutoTDP remain host policy.
CurveInputs provides legal editor starting points, not an observation or a write. AMD processor clocks,
curve/thermal controls and Intel undervolting use descriptor commands with exact model bounds.

## Direct commands and outcomes

ExecuteAsync accepts a CapabilityCommand with a fresh CommandId, exact capability/instance, typed
value, optional paired power target and Deadline. Prefer semantic methods where they fit.

| Outcome       | Meaning                                                                    |
| ------------- | -------------------------------------------------------------------------- |
| Applied       | Native transport accepted the write; independent readback remains separate |
| Accepted      | Deferred acceptance contract; not proof of a hardware write                |
| Rejected      | Refused before writing                                                     |
| TimedOut      | Deadline passed; application is uncertain                                  |
| Indeterminate | An interrupted write may have reached hardware                             |

Semantic results retain completed commands in dispatch order. Failure or uncertainty stops remaining
commands without automatically rolling back earlier writes. Retain partial results and their
reasons.

Accepted values remain authoritative for the cycle. Temperature, RPM and other native measurements
are separate sampled telemetry. Original-state captures for restoration do not become UI readback.
Missing readback never disables an available writable control. Unknown measurements and uncertain
setters never authorize automatic retries.
