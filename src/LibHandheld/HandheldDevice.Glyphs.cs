using System.Collections.ObjectModel;

namespace LibHandheld;

public sealed partial class HandheldDevice
{
    private static readonly IReadOnlyDictionary<string, string> MsiClawGlyphs = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["glyphs/assets/controller-full.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.controller-full.svg",
            ["glyphs/assets/controller-left.png"] = "LibHandheld.Families.MsiClaw.glyphs.assets.controller-left.png",
            ["glyphs/assets/controller-right.png"] = "LibHandheld.Families.MsiClaw.glyphs.assets.controller-right.png",
            ["glyphs/assets/dpad-down-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-down-highlight.svg",
            ["glyphs/assets/dpad-down.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-down.svg",
            ["glyphs/assets/dpad-left-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-left-highlight.svg",
            ["glyphs/assets/dpad-left.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-left.svg",
            ["glyphs/assets/dpad-right-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-right-highlight.svg",
            ["glyphs/assets/dpad-right.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-right.svg",
            ["glyphs/assets/dpad-up-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-up-highlight.svg",
            ["glyphs/assets/dpad-up.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.dpad-up.svg",
            ["glyphs/assets/face-east-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.face-east-highlight.svg",
            ["glyphs/assets/face-east.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.face-east.svg",
            ["glyphs/assets/face-north-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.face-north-highlight.svg",
            ["glyphs/assets/face-north.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.face-north.svg",
            ["glyphs/assets/face-south-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.face-south-highlight.svg",
            ["glyphs/assets/face-south.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.face-south.svg",
            ["glyphs/assets/face-west-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.face-west-highlight.svg",
            ["glyphs/assets/face-west.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.face-west.svg",
            ["glyphs/assets/guide-highlight.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.guide-highlight.svg",
            ["glyphs/assets/guide.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.guide.svg",
            ["glyphs/assets/left-shoulder-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.left-shoulder-highlight.svg",
            ["glyphs/assets/left-shoulder.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.left-shoulder.svg",
            ["glyphs/assets/left-stick-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.left-stick-highlight.svg",
            ["glyphs/assets/left-stick.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.left-stick.svg",
            ["glyphs/assets/left-trigger-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.left-trigger-highlight.svg",
            ["glyphs/assets/left-trigger-soft.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.left-trigger-soft.svg",
            ["glyphs/assets/left-trigger.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.left-trigger.svg",
            ["glyphs/assets/menu-highlight.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.menu-highlight.svg",
            ["glyphs/assets/menu.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.menu.svg",
            ["glyphs/assets/quick-access-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.quick-access-highlight.svg",
            ["glyphs/assets/quick-access.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.quick-access.svg",
            ["glyphs/assets/rear-m1-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.rear-m1-highlight.svg",
            ["glyphs/assets/rear-m1.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.rear-m1.svg",
            ["glyphs/assets/rear-m2-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.rear-m2-highlight.svg",
            ["glyphs/assets/rear-m2.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.rear-m2.svg",
            ["glyphs/assets/right-shoulder-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.right-shoulder-highlight.svg",
            ["glyphs/assets/right-shoulder.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.right-shoulder.svg",
            ["glyphs/assets/right-stick-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.right-stick-highlight.svg",
            ["glyphs/assets/right-stick.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.right-stick.svg",
            ["glyphs/assets/right-trigger-highlight.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.right-trigger-highlight.svg",
            ["glyphs/assets/right-trigger-soft.svg"] =
                "LibHandheld.Families.MsiClaw.glyphs.assets.right-trigger-soft.svg",
            ["glyphs/assets/right-trigger.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.right-trigger.svg",
            ["glyphs/assets/view-highlight.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.view-highlight.svg",
            ["glyphs/assets/view.svg"] = "LibHandheld.Families.MsiClaw.glyphs.assets.view.svg",
            ["glyphs/profiles/msi-claw.json"] = "LibHandheld.Families.MsiClaw.glyphs.profiles.msi-claw.json",
            ["THIRD_PARTY_NOTICES.md"] = "LibHandheld.Families.MsiClaw.THIRD_PARTY_NOTICES.md"
        });

    private static readonly IReadOnlyDictionary<string, string> RogAllyGlyphs = new ReadOnlyDictionary<string, string>(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["glyphs/assets/armoury-crate.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.armoury-crate.svg",
            ["glyphs/assets/command-center.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.command-center.svg",
            ["glyphs/assets/controller-full-rog-ally.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.controller-full-rog-ally.svg",
            ["glyphs/assets/controller-full-rog-xbox-ally.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.controller-full-rog-xbox-ally.svg",
            ["glyphs/assets/controller-left-rog-ally.png"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.controller-left-rog-ally.png",
            ["glyphs/assets/controller-left-rog-xbox-ally.png"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.controller-left-rog-xbox-ally.png",
            ["glyphs/assets/controller-right-rog-ally.png"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.controller-right-rog-ally.png",
            ["glyphs/assets/controller-right-rog-xbox-ally.png"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.controller-right-rog-xbox-ally.png",
            ["glyphs/assets/dpad-down.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.dpad-down.svg",
            ["glyphs/assets/dpad-left.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.dpad-left.svg",
            ["glyphs/assets/dpad-right.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.dpad-right.svg",
            ["glyphs/assets/dpad-up.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.dpad-up.svg",
            ["glyphs/assets/dpad.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.dpad.svg",
            ["glyphs/assets/face-east.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.face-east.svg",
            ["glyphs/assets/face-north.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.face-north.svg",
            ["glyphs/assets/face-south.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.face-south.svg",
            ["glyphs/assets/face-west.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.face-west.svg",
            ["glyphs/assets/left-shoulder.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-shoulder.svg",
            ["glyphs/assets/left-stick-down.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-stick-down.svg",
            ["glyphs/assets/left-stick-left.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-stick-left.svg",
            ["glyphs/assets/left-stick-neutral.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.left-stick-neutral.svg",
            ["glyphs/assets/left-stick-right.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-stick-right.svg",
            ["glyphs/assets/left-stick-up.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-stick-up.svg",
            ["glyphs/assets/left-stick.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-stick.svg",
            ["glyphs/assets/left-trigger-soft.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.left-trigger-soft.svg",
            ["glyphs/assets/left-trigger.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.left-trigger.svg",
            ["glyphs/assets/library.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.library.svg",
            ["glyphs/assets/menu.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.menu.svg",
            ["glyphs/assets/mono-a.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.mono-a.svg",
            ["glyphs/assets/mono-b.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.mono-b.svg",
            ["glyphs/assets/mono-x.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.mono-x.svg",
            ["glyphs/assets/mono-y.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.mono-y.svg",
            ["glyphs/assets/rear-m1-right.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.rear-m1-right.svg",
            ["glyphs/assets/rear-m1.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.rear-m1.svg",
            ["glyphs/assets/rear-m2-left.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.rear-m2-left.svg",
            ["glyphs/assets/rear-m2.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.rear-m2.svg",
            ["glyphs/assets/right-shoulder.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.right-shoulder.svg",
            ["glyphs/assets/right-stick-down.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.right-stick-down.svg",
            ["glyphs/assets/right-stick-left.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.right-stick-left.svg",
            ["glyphs/assets/right-stick-neutral.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.right-stick-neutral.svg",
            ["glyphs/assets/right-stick-right.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.right-stick-right.svg",
            ["glyphs/assets/right-stick-up.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.right-stick-up.svg",
            ["glyphs/assets/right-stick.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.right-stick.svg",
            ["glyphs/assets/right-trigger-soft.svg"] =
                "LibHandheld.Families.RogAlly.glyphs.assets.right-trigger-soft.svg",
            ["glyphs/assets/right-trigger.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.right-trigger.svg",
            ["glyphs/assets/view.svg"] = "LibHandheld.Families.RogAlly.glyphs.assets.view.svg",
            ["glyphs/profiles/rog-ally.json"] = "LibHandheld.Families.RogAlly.glyphs.profiles.rog-ally.json",
            ["glyphs/profiles/rog-xbox-ally.json"] = "LibHandheld.Families.RogAlly.glyphs.profiles.rog-xbox-ally.json",
            ["THIRD_PARTY_NOTICES.md"] = "LibHandheld.Families.RogAlly.THIRD_PARTY_NOTICES.md"
        });

    private IReadOnlyDictionary<string, string> GlyphMap =>
        Definition.FamilyId == "wsgm.device.msi.claw" ? MsiClawGlyphs : RogAllyGlyphs;

    /// <summary>Exact allowlisted authored glyph paths and attribution notice, relative to the family resource root.</summary>
    public IReadOnlyList<string> GlyphResources => Array.AsReadOnly(GlyphMap.Keys.ToArray());

    /// <summary>Opens original authored bytes for an allowlisted path. The caller validates content and disposes the stream.</summary>
    /// <param name="relativePath">One path in GlyphResources, compared ordinally.</param>
    /// <returns>The original embedded bytes; no parsing, rewriting or execution occurs.</returns>
    public Stream OpenGlyphResource(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (!GlyphMap.TryGetValue(relativePath, out var resource))
        {
            throw new ArgumentException("Unknown authored glyph resource.", nameof(relativePath));
        }

        return typeof(HandheldDevice).Assembly.GetManifestResourceStream(resource) ??
               throw new InvalidOperationException($"Missing embedded glyph resource {resource}.");
    }
}
