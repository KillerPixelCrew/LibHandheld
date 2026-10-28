# Implementation provenance

The initial MSI Claw and ASUS ROG Ally engines are extracted from KillerPixelCrew's MIT-licensed
implementations in WSGM at source commit `3b0c1e1c8268268a14c767c2efc07bdc893c3f84`. Their native
protocols, recovery rules, physical input, motion and haptic behavior are preserved during
extraction. The family provenance and authored glyph notices remain with each engine.

Family protocol references and authored glyph attribution remain beside their implementations under
`src/LibHandheld/Families`. [Supported devices](docs/devices.md) describes the public catalog.
Reference support and compilation are separate from hardware observations on a particular model.

Processor reset follows the same source contracts as the writes in `ProfiledProcessorService` and
`AmdSmuTransport.Settings`: HC's AMD processor restores a minimum/maximum clock range only for its
older min/max command path; its fixed-clock path returns without a restore command. Intel has no GPU
clock setting. Zero removes curve/undervolt offsets. Thermal settings use captured
originals, with no invented factory temperature. Unrestorable settings explicitly require Windows
restart after the consumer clears its saved overrides. These are source contracts, not hardware
acceptance of the reset.

## Intel register access

Intel package power limits and OC-mailbox voltage offsets use the signed PawnIO IntelMSR module
embedded in this library ([module source](https://github.com/namazso/PawnIO.Modules/blob/main/IntelMSR.p)).
Power writes replace only the PL1 and PL2 fields of MSR 0x610, set both enable bits and keep the time
windows, clamp and lock bits; a locked register is refused without a write. The unit comes from MSR
0x606. Handheld Companion's KX helper also writes the MCHBAR power mirror at 0x59A0/0x59A4 and the
GPU clock at MCHBAR 0x5994. PawnIO's MCHBAR module is read-only, so the mirror is left as the
firmware set it and Intel GPU clock control is not offered. KX and its HwRwDrv driver, which Windows
blocks, are no longer used.
