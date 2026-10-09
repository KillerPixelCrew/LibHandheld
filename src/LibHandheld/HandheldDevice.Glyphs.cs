using System.Collections.ObjectModel;

namespace LibHandheld;

public sealed partial class HandheldDevice
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> GlyphMaps =
        BuildGlyphMaps();

    private static readonly IReadOnlyDictionary<string, string> NoAuthoredGlyphs =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());

    private IReadOnlyDictionary<string, string> GlyphMap =>
        GlyphMaps.TryGetValue(Definition.GlyphProfileId, out var resources) ? resources : NoAuthoredGlyphs;

    /// <summary>Authored glyph paths and attribution notice derived from this definition's embedded glyph profile.</summary>
    public IReadOnlyList<string> GlyphResources => Array.AsReadOnly(GlyphMap.Keys.ToArray());

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuildGlyphMaps()
    {
        const string profiles = ".glyphs.profiles.";
        var names = typeof(HandheldDevice).Assembly.GetManifestResourceNames();
        var maps = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var profile in names)
        {
            var index = profile.IndexOf(profiles, StringComparison.Ordinal);
            if (!profile.StartsWith("LibHandheld.Families.", StringComparison.Ordinal) || index < 0 ||
                !profile.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            var family = profile[..(index + 1)];
            var resources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                foreach (var folder in new[] { "assets", "profiles" })
                {
                    var prefix = family + "glyphs." + folder + ".";
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        resources.Add("glyphs/" + folder + "/" + name[prefix.Length..], name);
                    }
                }
            }

            var notice = family + "THIRD_PARTY_NOTICES.md";
            if (names.Contains(notice, StringComparer.Ordinal))
            {
                resources.Add("THIRD_PARTY_NOTICES.md", notice);
            }

            maps.Add(profile[(index + profiles.Length)..^5], new ReadOnlyDictionary<string, string>(resources));
        }

        return new ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>(maps);
    }

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
