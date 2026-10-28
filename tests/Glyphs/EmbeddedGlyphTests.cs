using System.Text.Json;
using System.Xml.Linq;
using LibHandheld.Contracts;
using LibHandheld.Internal;

namespace LibHandheld.Tests.Glyphs;

public sealed class EmbeddedGlyphTests
{
    [Fact]
    public async Task EveryDefinitionHasAnExactEmbeddedProfileAndAttribution()
    {
        foreach (var definition in HandheldDevice.Definitions)
        {
            await using var device = Create(definition);
            var profilePath = $"glyphs/profiles/{definition.GlyphProfileId}.json";
            Assert.Contains(profilePath, device.GlyphResources);
            using var profileStream = device.OpenGlyphResource(profilePath);
            using var profile = JsonDocument.Parse(profileStream);
            Assert.Equal(definition.GlyphProfileId, profile.RootElement.GetProperty("profileId").GetString());
            Assert.Contains(definition.Id,
                profile.RootElement.GetProperty("exactDeviceIds").EnumerateArray().Select(id => id.GetString()));
            using var notice = device.OpenGlyphResource(profile.RootElement.GetProperty("noticePath").GetString()!);
            Assert.True(notice.Length > 0, definition.Id);
            if (!definition.HasController)
            {
                Assert.DoesNotContain(profile.RootElement.GetProperty("controls").EnumerateArray(),
                    mapping => mapping.GetProperty("control").GetString() == "FaceSouth"
                               && mapping.GetProperty("presence").GetString() == "Present");
            }
        }
    }

    [Fact]
    public async Task EveryDeclaredAssetIsPackagedAndHasItsDeclaredDimensions()
    {
        foreach (var definition in HandheldDevice.Definitions)
        {
            await using var device = Create(definition);
            using var stream = device.OpenGlyphResource($"glyphs/profiles/{definition.GlyphProfileId}.json");
            using var profile = JsonDocument.Parse(stream);
            var assets = profile.RootElement.GetProperty("assets").EnumerateArray()
                .ToDictionary(asset => asset.GetProperty("assetId").GetString()!, StringComparer.Ordinal);
            foreach (var asset in assets.Values)
            {
                var extension = asset.GetProperty("format").GetString() == "Svg" ? ".svg" : ".png";
                using var artwork = device.OpenGlyphResource(
                    "glyphs/assets/" + asset.GetProperty("assetId").GetString() + extension);
                Assert.True(artwork.Length > 0, definition.Id);
                if (extension != ".svg")
                {
                    continue;
                }

                var svg = XDocument.Load(artwork).Root!;
                var viewBox = svg.Attribute("viewBox")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              ?? ["0", "0", svg.Attribute("width")!.Value, svg.Attribute("height")!.Value];
                Assert.Equal(4, viewBox.Length);
                var expected = asset.GetProperty("viewBox");
                Assert.Equal(expected.GetProperty("width").GetDecimal(),
                    decimal.Parse(viewBox[2], System.Globalization.CultureInfo.InvariantCulture));
                Assert.Equal(expected.GetProperty("height").GetDecimal(),
                    decimal.Parse(viewBox[3], System.Globalization.CultureInfo.InvariantCulture));
                if (asset.GetProperty("role").GetString() == "Control")
                {
                    Assert.Contains(svg.Descendants(), path => path.Name.LocalName == "path"
                                                              && path.Attribute("d")?.Value.Length > 0);
                }
            }

            foreach (var mapping in profile.RootElement.GetProperty("controls").EnumerateArray())
            {
                foreach (var reference in new[] { "assetId", "softPullAssetId", "highlightAssetId" })
                {
                    if (mapping.TryGetProperty(reference, out var assetId))
                    {
                        Assert.Contains(assetId.GetString()!, assets.Keys);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("lenovo.legion-go.83e1", "RearM1", "Y1", "y1", "pf-2215")]
    [InlineData("lenovo.legion-go.83e1", "RearM2", "M3", "m3", "pf-2214")]
    [InlineData("lenovo.legion-go.83e1", "RearLeft2", "Y2", "y2", "pf-2216")]
    [InlineData("lenovo.legion-go.83e1", "RearRight2", "Y3", "y3", "pf-2217")]
    [InlineData("ayaneo.3-hx370", "RearM1", "L4", "l4", "pf-2276")]
    [InlineData("ayaneo.3-hx370", "RearM2", "R4", "r4", "pf-2277")]
    [InlineData("ayaneo.3-hx370", "RearLeft2", "L5", "l5", "pf-2278")]
    [InlineData("ayaneo.3-hx370", "RearRight2", "R5", "r5", "pf-2279")]
    // The Atlas prints M2 on its left rear button and M1 on its right one.
    [InlineData("acer-predator-atlas-8-pa08-i51", "RearM1", "M2", "m2", "rear-m2-left")]
    [InlineData("acer-predator-atlas-8-pa08-i51", "RearM2", "M1", "m1", "rear-m1-right")]
    public async Task RearArtworkTracksThePhysicalControl(string id, string control, string label,
        string nativeId, string asset)
    {
        await using var device = Create(HandheldDevice.Definitions.Single(definition => definition.Id == id));
        using var stream = device.OpenGlyphResource($"glyphs/profiles/{device.Definition.GlyphProfileId}.json");
        using var profile = JsonDocument.Parse(stream);
        var mapping = profile.RootElement.GetProperty("controls").EnumerateArray()
            .Single(candidate => candidate.GetProperty("control").GetString() == control);
        Assert.Equal(label, mapping.GetProperty("physicalLabel").GetString());
        Assert.Equal(nativeId, mapping.GetProperty("oemControlId").GetString());
        Assert.Equal(asset, mapping.GetProperty("assetId").GetString());
    }

    private static HandheldDevice Create(HandheldDefinition definition)
    {
        // Reading authored resources never starts a service or opens a hardware transport.
        return new HandheldDevice(definition, new DeviceIdentitySnapshot(), "glyph-resource-test", null,
            DeviceFamilies.Create(definition));
    }
}
