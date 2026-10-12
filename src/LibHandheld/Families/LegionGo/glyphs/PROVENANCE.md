# Glyph provenance

HC reference: `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` (1.3.3.1), Devices `GetGlyph` overrides and
Controllers `IController`, `Xbox360Controller`, `SteamController`, `LegionController`,
`AYANEODController`. PromptFont original SHA-256:
`b190b52ef647e20e8850f5f4c3a972d2c5cf0e14e4c3e104b90d65646cb7559a`. Assets retain original contours
and OFL attribution.

| Exact profile           | Authored assets | Native OEM mapping                                                                                                                                                                                                                                 |
| ----------------------- | --------------: | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| lenovo-legion-go-83e1   |              41 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, m3=pf-2214, y2=pf-2216, y3=pf-2217, m1=pf-2212, m2=pf-2213, page=fluent-tiles, desktop=fluent-dialpad, wheel-click=pf-2206, wheel-up=pf-27f0, wheel-down=pf-27f1, touchpad-secondary-click=pf-27f7 |
| lenovo-legion-go-83n0   |              41 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, m3=pf-2214, y2=pf-2216, y3=pf-2217, m1=pf-2212, m2=pf-2213, page=fluent-tiles, desktop=fluent-dialpad, wheel-click=pf-2206, wheel-up=pf-27f0, wheel-down=pf-27f1, touchpad-secondary-click=pf-27f7 |
| lenovo-legion-go-83n1   |              41 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, m3=pf-2214, y2=pf-2216, y3=pf-2217, m1=pf-2212, m2=pf-2213, page=fluent-tiles, desktop=fluent-dialpad, wheel-click=pf-2206, wheel-up=pf-27f0, wheel-down=pf-27f1, touchpad-secondary-click=pf-27f7 |
| lenovo-legion-go-s-83l3 |              33 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, y2=pf-2216                                                                                                                                                                                         |
| lenovo-legion-go-s-83n6 |              33 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, y2=pf-2216                                                                                                                                                                                         |
| lenovo-legion-go-s-83q2 |              33 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, y2=pf-2216                                                                                                                                                                                         |
| lenovo-legion-go-s-83q3 |              33 | legion-r=pf-2205, legion-l=pf-e004, y1=pf-2215, y2=pf-2216                                                                                                                                                                                         |

Controller images are optional and are not added where HC provides only font glyphs. HC references
ECA5 (Page) and E75F (Desktop) in PromptFont, but the font has neither codepoint. The attributed MIT
Fluent Tiles/Dialpad equivalents supply these intended symbols; see the family notice and Microsoft
catalog. Canonical rear glyphs follow L4=Y1, L5=Y2, R4=M3, R5=Y3; separate mouse M1/M2 controls
retain their own glyphs.
