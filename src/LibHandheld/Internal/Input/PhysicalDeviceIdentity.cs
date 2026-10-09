namespace LibHandheld.Internal.Input;

/// <summary>A physical input device owned by a plugin and eligible for WSGM's HidHide transaction.</summary>
internal sealed record PhysicalDeviceIdentity
{
    /// <summary>The device instance path, used verbatim as the HidHide entry.</summary>
    public required string InstancePath { get; init; }

    /// <summary>Physical USB location, for correlating across re-enumeration.</summary>
    public string? LocationPath { get; init; }

    /// <summary>USB vendor ID, four uppercase hexadecimal digits.</summary>
    public string? VendorId { get; init; }

    /// <summary>USB product ID, four uppercase hexadecimal digits.</summary>
    public string? ProductId { get; init; }

    /// <summary>Whether hiding this interface is required for controller management.</summary>
    public bool RequiresHiding { get; init; }
}
