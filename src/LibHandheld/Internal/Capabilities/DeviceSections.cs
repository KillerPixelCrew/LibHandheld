using LibHandheld.Internal.Settings;

namespace LibHandheld.Internal.Capabilities;

/// <summary>Shared Device pages to which WSGM and plugins contribute controls.</summary>
internal static class DeviceSections
{
    /// <summary>Power limits, profiles, cooling and Windows energy controls.</summary>
    public const string PowerId = "power";

    /// <summary>RGB lighting controls.</summary>
    public const string RgbId = "rgb";

    /// <summary>Controller, motion and input controls.</summary>
    public const string ControllerId = "controller";

    /// <summary>Readings, ownership and diagnostics.</summary>
    public const string InfoId = "info";

    /// <summary>The predefined Power page. A plugin may add categories using a record copy.</summary>
    public static CapabilitySection Power { get; } = new()
        { SectionId = PowerId, Key = SettingSectionKey.Power, Icon = SectionIcon.Power, SortOrder = 0 };

    /// <summary>The predefined RGB page.</summary>
    public static CapabilitySection Rgb { get; } = new()
        { SectionId = RgbId, Key = SettingSectionKey.Lighting, Icon = SectionIcon.Lighting, SortOrder = 1 };

    /// <summary>The predefined Controller page.</summary>
    public static CapabilitySection Controller { get; } = new()
        { SectionId = ControllerId, Key = SettingSectionKey.Controller, Icon = SectionIcon.Controller, SortOrder = 2 };

    /// <summary>The predefined Info page.</summary>
    public static CapabilitySection Info { get; } = new()
        { SectionId = InfoId, Key = SettingSectionKey.Diagnostics, Icon = SectionIcon.Gauge, SortOrder = 3 };

    /// <summary>The four shared page declarations in their default presentation order.</summary>
    /// <remarks>
    ///     Descriptors may reference these IDs without declaring the sections; WSGM adds every shared page a
    ///     plugin did not declare. Custom sections still require explicit declarations.
    /// </remarks>
    public static IReadOnlyList<CapabilitySection> All { get; } =
        Array.AsReadOnly<CapabilitySection>([Power, Rgb, Controller, Info]);
}
