# Acer glyph provenance

Standard gamepad glyphs (face buttons, D-pad, sticks, shoulders and triggers) reuse the library's
existing PromptFont outlines from the Generic family, with their original SIL OFL attribution in
`THIRD_PARTY_NOTICES.md`. Their source revision and font digest remain unchanged.

The four front OEM buttons use original drawings made for this library on 2026-10-28 from the
maintainer's photographs of an Atlas 8, in the PromptFont disc style with the print cut out:

| Asset                | Button      | Print reconstructed                                      |
| -------------------- | ----------- | -------------------------------------------------------- |
| `atlas-task.svg`     | front left  | slanted key with a short bar, the Task button            |
| `atlas-view.svg`     | lower left  | two overlapping squares, the front one with a centre bar |
| `atlas-predator.svg` | front right | mirrored claw mark, the Predator button                  |
| `atlas-menu.svg`     | lower right | three lines                                              |

No PredatorSense file is copied or traced: its gamepad illustration leaves the left buttons blank,
and the Predator mark is redrawn from the photograph as a simplified shape. The rear badges
`rear-m1-right.svg` and `rear-m2-left.svg` are the MIT `handheld-controller-glyphs` files already
shipped by the ROG Ally family, copied unmodified; see `THIRD_PARTY_NOTICES.md`.

Rear sides follow the 2026-10-28 attended capture: the left rear button is printed M2 and is the
canonical left paddle (`RearM1`, L4); the right rear button is printed M1 (`RearM2`, R4).
