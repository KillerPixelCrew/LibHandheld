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
older min/max command path; its fixed-clock path returns without a restore command. Intel uses HC's
12750 MHz automatic sentinel. Zero removes curve/undervolt offsets. Thermal settings use captured
originals, with no invented factory temperature. Unrestorable settings explicitly require Windows
restart after the consumer clears its saved overrides. These are source contracts, not hardware
acceptance of the reset.
