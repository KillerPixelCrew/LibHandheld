# Atlas 8 power-limit investigation, 2026-10-27

This records live tests on Acer Predator PA08-I51, BIOS V1.03, Intel Arc G3, on AC with Acer OEM
services and packages disabled. Profile storage acceptance and measured package power are separate
results.

## Profile selection and stored range

Custom limits belong to Manual mode 3. Acer's desktop `SystemStateModel.SetManualOperatingModeAsync`
sends `OPERATING_MODE` with mode 3, PL1 and PL2. HardwareService reads profile 3, changes its two
integer-watt fields, writes the retained profile, then selects mode 3. The probe uses that same
sequence. There is no additional custom-profile activation in the traced dispatch.

The firmware advertises PL1 10..40 W and PL2 maximum 65 W. All 145 profile-only cases acknowledged
and read back exactly: PL1 10..45 W, PL2 10..80 W, unified 10..45 W, plus unified 8 and 9 W. Integer
1 W steps worked throughout the covered ranges. These are tested storage values, not discovered
absolute boundaries or evidence that the processor consumes those watts. Uninterpreted fields stayed
unchanged. Each test restored the original manual 25/45 W profile and active Turbo mode; recovery
journals ended healthy.

Raw evidence: local `artifacts/acer-tdp-range-results.json`, `acer-tdp-unified-upper-results.json`
and their trial logs. Production bounds remain the advertised range.

## CPU-only load pilot

Fourteen below-normal AVX/FMA workers stressed the CPU. Both fans temporarily used Full Speed.
Package watts came from unsigned deltas of Intel RAPL package energy MSR 0x611, using units read
from 0x606 and monotonic elapsed time. Temperature came from package thermal status 0x1B1 with TjMax
read from 0x1A2. No power registers were written. The trial stops at 90 C, AC loss, its deadline, or
the explicit stop file, and restores original power and fan state.

A repeat with MMIO observation gave these 20-second stages:

| Unified requested watts | Last ten samples, mean package watts | Maximum package temperature |
| ----------------------- | ------------------------------------ | --------------------------- |
| 25                      | 24.60                                | 67 C                        |
| 45                      | 31.53                                | 82 C                        |
| 60                      | 31.16                                | 79 C                        |

Neither stage above 25 W showed sampled thermal throttling. The CPU-only plateau does not establish
a BIOS ceiling because this workload may not demand the full CPU/GPU package budget. MSR 0x610
stayed `0000820800DD8168`; MCHBAR 0x59A0 stayed `0000820800DF8168`. Both encode enabled PL1 45 W and
PL2 65 W with the measured 0.125 W unit, despite different Acer profile settings. Low custom
settings can still affect power through other platform controls.

Raw evidence: local `artifacts/acer-tdp-load-pilot.jsonl`, its summary and log. Cleanup reported
power restored/released, fans released and a healthy journal.

## Read-only measurement dependency

The signed PawnIO 2.2.0 driver was installed without unrestricted mode or changes to driver-signing
enforcement. IntelMSR and IntelMCHBAR modules came from the pinned upstream PawnIO.Modules 0.2.11
archive. The private probe loaded the signed IntelMCHBAR module only for its read-only
`ioctl_read_qword` at the already sourced package-limit offset 0x59A0. It did not scan physical
memory or write MSR/MMIO. IntelMCHBAR SHA256:
`3F82B832D99B4AAC37D2A20FDB7C9BAA2A3BC0488612C9019C9484EB0E8A6EAE`. This additional measurement
module is not a production LibHandheld dependency.

Intel's Arc G3 specifications distinguish 25 W processor base power, 80 W maximum turbo power and
8..30 W configurable TDP. Those specifications are not proof of this handheld's cooling, charger
capacity or sustained firmware allowance:
[official Intel specifications](https://www.intel.com/content/www/us/en/products/sku/245626/intel-arc-g3-processor-12m-cache-up-to-4-60-ghz/specifications.html).

## OCCT observation and supported library policy

The maintainer then ran OCCT Personal 17.1.10's CPU stress workload. A live monitoring screenshot
showed 47.0 W CPU package power, 100% CPU utilization, 100 C package temperature and approximately
3642 MHz. Earlier views showed about 40 W at 99 C. These are visual samples from the maintainer's
run, not a controlled custom-limit sweep. They establish that 31 W is not an absolute hardware
ceiling, but do not establish a sustained maximum or which custom profile was active. At 100 C,
thermal constraints confound further conclusions. The displayed GPU utilization was negligible; this
was not proof of a combined CPU/GPU stress test. The last inspection left the maintainer's OCCT run
active.

The library deliberately retains firmware-advertised capabilities: PL1 10..40 W, PL2 10..65 W with
PL2 >= PL1, integer 1 W steps, Manual mode 3 for custom writes, and recovery of the original profile
and active mode when ownership ends. Values represent configured limits, not guaranteed consumption.
Neither the wider accepted storage range nor Intel's CPU specifications widen normal bounds. A
controlled low/high load sweep remains unperformed; 1..80 W is not verified. The existing runtime
already implements this policy, so no production power-code change was required for these findings.

On 2026-10-29 the floor of both limits moved to 8 W after the maintainer confirmed 8 W on the device;
see the [provenance](PROVENANCE.md#lower-power-tiers-2026-10-29). The maxima are unchanged.

OCCT was obtained from its [official download page](https://www.ocbase.com/download-personal). The
executable matched the published SHA256
`64F403FB690AFA724B3BF31F439F8A29C81C34B4D3496A63456A9AB224E9599B` and had a valid OCBASE
Authenticode signature. Local evidence includes `artifacts/occt-screen.png`; this mutable desktop
capture is supporting evidence, not a reproducible sustained-power trace or a committed fixture.
