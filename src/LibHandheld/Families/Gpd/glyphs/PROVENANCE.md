# Glyph provenance

HC reference: `a250b19a4b7e29dac3b383f2d1ac517d1af43e4e` (1.3.3.1), Devices `GetGlyph` overrides and
Controllers `IController`, `Xbox360Controller`, `SteamController`, `LegionController`,
`AYANEODController`. PromptFont original SHA-256:
`b190b52ef647e20e8850f5f4c3a972d2c5cf0e14e4c3e104b90d65646cb7559a`. Assets retain original contours
and OFL attribution.

| Profile       | Exact devices                                          | Authored assets | Native OEM mapping                                                                    |
| ------------- | ------------------------------------------------------ | --------------: | ------------------------------------------------------------------------------------- |
| gpd-win2      | gpd-win2                                               |              26 |                                                                                       |
| gpd-win3-win4 | gpd-win3, gpd-win4-6800u/7640u/7840u/8640u/8840u/hx370 |              28 | l4=pf-220e, r4=pf-220f                                                                |
| gpd-win-mini  | gpd-win-mini-7640u/7840u/8840u/hx370                   |              30 | l4=pf-2276, r4=pf-2277                                                                |
| gpd-win-max2  | gpd-win-max2-intel/6800u/7640u/7840u/8640u/8840u/hx370 |              31 | l4=pf-220e, r4=pf-220f, menu=pf-221a                                                  |
| gpd-win5      | gpd-win5                                               |              32 | l4=pf-2276, r4=pf-2277, menu=pf-221a, keyboard=pf-243d, home=pf-21f9, gamepad=pf-243c |

Processor variants of one body carry identical controls and artwork, so they share one profile. The
WIN 3 has the same controls and artwork as the WIN 4 and shares its profile.

Controller images are optional and are not added where HC provides only font glyphs.
