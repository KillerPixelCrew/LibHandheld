# Third-party notices

## Steam's own glyph

The ROG Xbox Ally profile draws its Xbox button in Steam with `xbox_button_logo`, a glyph Steam
ships under `/steaminputglyphs/`. The profile names it; the package carries no copy.

## Handheld controller glyphs

The package's physical glyph artwork under `glyphs/assets/` comes from the
`handheld-controller-glyphs` project at revision `46792aadf3b104efec1c5240ba414d2c0bf84127`,
licensed under the MIT License. Every ASUS asset (the ROG Ally and ROG Xbox Ally controller images,
the ASUS face buttons in colour and mono, and the Command Center, Armoury Crate and Library marks)
and the shared D-pad, stick, shoulder, trigger, view, menu and rear-button glyphs are copied
unmodified. Each file keeps the original bytes under a name that says what it depicts.

Copyright (c) 2024 victor-borges

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute,
sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## Reference implementations

No source code from Handheld Companion (CC BY-NC-SA 4.0) or HHD (LGPL-2.1) is included. Both were
read as references for device facts: report IDs, command bytes, ATKACPI device IDs, axis maps and
power limits. The plugin's code is written for this package under its MIT licence, and every fact
taken from them is cited in `PROVENANCE.md`.

## Additional HC parity glyphs

The `pf-*.svg` stick-direction and Xbox-guide outlines come from original HC PromptFont at revision
`a250b19a4b7e29dac3b383f2d1ac517d1af43e4e`, SHA-256
`b190b52ef647e20e8850f5f4c3a972d2c5cf0e14e4c3e104b90d65646cb7559a`. Contours and proportions are
unchanged; only SVG coordinates and white paint are selected. Copyright 2018-2026, Yukari Hafner.
Copyright 2011-2016, Severin Meyer. PromptFont by Shinmera (Yukari Hafner),
https://shinmera.com/promptfont. SIL OFL 1.1 above applies.
