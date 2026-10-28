namespace LibHandheld.Contracts;

/// <summary>
///     The normalized machine facts a device definition's identity observations are matched against.
/// </summary>
/// <remarks>
///     This is the observed half of identity. The backend's exact model predicates match what the
///     machine actually reports.
///     Producing it is platform work owned by the host runtime; the contract only fixes which
///     facts exist and how they compare, so both sides agree on what "matched" means.
///     <para>
///         Every value arrives with normalized whitespace. Comparison is ordinal
///         and case-insensitive on the normalized form, so a vendor that changes casing or padding between
///         firmware revisions does not silently stop matching.
///     </para>
/// </remarks>
public sealed record DeviceIdentitySnapshot
{
    /// <summary>SMBIOS Type 1 manufacturer.</summary>
    public string? SystemManufacturer { get; init; }

    /// <summary>SMBIOS Type 1 product name. Marketing text.</summary>
    public string? SystemProduct { get; init; }

    /// <summary>SMBIOS Type 1 SKU number.</summary>
    public string? SystemSku { get; init; }

    /// <summary>SMBIOS Type 1 family.</summary>
    public string? SystemFamily { get; init; }

    /// <summary>SMBIOS Type 2 baseboard manufacturer.</summary>
    public string? BaseboardManufacturer { get; init; }

    /// <summary>SMBIOS Type 2 baseboard product — the exact board identifier.</summary>
    public string? BaseboardProduct { get; init; }

    /// <summary>SMBIOS Type 2 baseboard version or revision.</summary>
    public string? BaseboardVersion { get; init; }

    /// <summary>System BIOS version string.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>
    ///     Embedded-controller firmware version, sourced from the vendor provider rather than SMBIOS.
    /// </summary>
    public string? EcFirmwareVersion { get; init; }

    /// <summary>Controller or MCU firmware version.</summary>
    public string? McuFirmwareVersion { get; init; }

    /// <summary>CPU family, model, and stepping in a normalized <c>family-model-stepping</c> form.</summary>
    public string? CpuIdentity { get; init; }

    /// <summary>Processor brand string, for example <c>Intel(R) Core(TM) Ultra 7 258V</c>.</summary>
    public string? ProcessorName { get; init; }

    /// <summary>USB endpoints currently present on this machine.</summary>
    public IReadOnlyList<UsbEndpointObservation> UsbEndpoints { get; init; } = [];

    /// <summary>
    ///     Signatures of WMI providers, classes, or methods found present.
    /// </summary>
    /// <remarks>
    ///     Presence only. A method being enumerable never authorizes invoking it — the inventory records
    ///     signatures precisely so a definition can gate on availability without anything calling it.
    /// </remarks>
    public IReadOnlyList<string> WmiProviderSignatures { get; init; } = [];
}

/// <summary>
///     One observed USB endpoint.
/// </summary>
public sealed record UsbEndpointObservation
{
    /// <summary>USB vendor ID, four uppercase hexadecimal digits.</summary>
    public required string VendorId { get; init; }

    /// <summary>USB product ID, four uppercase hexadecimal digits.</summary>
    public required string ProductId { get; init; }

    /// <summary>Interface number when this endpoint is one interface of a composite device.</summary>
    public int? InterfaceNumber { get; init; }

    /// <summary>USB <c>bcdDevice</c>, four uppercase hexadecimal digits.</summary>
    public string? DeviceRelease { get; init; }

    /// <summary>Hash of this endpoint's HID report descriptor.</summary>
    public string? ReportDescriptorHash { get; init; }

    /// <summary>Observed HID report lengths in bytes.</summary>
    public IReadOnlyList<int> ReportLengths { get; init; } = [];

    /// <summary>
    ///     Physical USB location path, used to follow this endpoint across re-enumeration.
    /// </summary>
    /// <remarks>
    ///     Deliberately diagnostic-only and unusable as a package-manifest predicate. A location path
    ///     describes which port a device is plugged into on *this* machine and
    ///     differs between units of the same model, so gating on it would match one developer's unit and
    ///     nothing else. It is the continuation key for hotplug and controller mode changes — the only
    ///     identifier verified stable across a full mode-switch cycle, since container ID is the null
    ///     GUID on the reference hardware and the USB serial exists in only one of the two modes.
    /// </remarks>
    public string? LocationPath { get; init; }
}
