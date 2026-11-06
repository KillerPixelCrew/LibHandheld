namespace LibHandheld.Contracts;

/// <summary>An OEM graphics-package requirement, scoped to an explicit PCI vendor on an exact model.</summary>
/// <param name="PciVendorId">Four hexadecimal PCI vendor digits, without the VEN_ prefix.</param>
/// <param name="Reason">The refusal and official OEM support source shown by the consumer.</param>
public sealed record GraphicsDriverUpdateRestriction(string PciVendorId, string Reason)
{
    /// <summary>Returns the restriction only when the adapter's exact vendor field matches.</summary>
    /// <param name="hardwareId">The PCI adapter's hardware fields, such as VEN_1002&amp;DEV_15BF.</param>
    /// <returns>The reason, or null for an adapter this fact does not cover.</returns>
    public string? ReasonFor(string hardwareId)
    {
        return hardwareId.Split('&').Any(field => string.Equals(field, "VEN_" + PciVendorId,
            StringComparison.OrdinalIgnoreCase)) ? Reason : null;
    }
}

/// <summary>Documented vendor restrictions explicitly adopted by AMD handheld definitions.</summary>
public static class GraphicsDriverUpdateRestrictions
{
    /// <summary>AMD excludes handheld gaming devices from its generic Adrenalin package.</summary>
    /// <remarks>
    ///     Source: AMD Adrenalin 26.10.2 release notes, Important Notes, retrieved 2026-11-06:
    ///     https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-26-10-2.html .
    ///     Only AMD handheld definitions opt into this fact; Intel models and general PCs default to no restriction.
    /// </remarks>
    public static GraphicsDriverUpdateRestriction AmdHandheld { get; } = new("1002",
        "AMD's generic Adrenalin packages do not support handheld gaming devices. Use the device manufacturer's "
        + "graphics driver. See https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-26-10-2.html .");
}
