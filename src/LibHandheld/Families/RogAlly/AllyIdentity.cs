using LibHandheld.Contracts;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>SMBIOS model and BIOS binding captured for an acquisition, with an AC-line observation.</summary>
internal sealed record AllyIdentityState
{
    public required DeviceIdentitySnapshot Snapshot { get; init; }

    /// <summary>The model the live SMBIOS identity resolves to, or null when it no longer matches.</summary>
    public AllyModel? Model { get; init; }

    public required bool OnAcPower { get; init; }

    public bool ExactMachineMatch => Model is not null;

    /// <summary>Firmware identity recovery journal entries are bound to.</summary>
    /// <remarks>
    ///     The BIOS version, because it carries the ACPI methods ATKACPI dispatches to. An entry
    ///     written under one BIOS is not replayed under another.
    /// </remarks>
    public string FirmwareIdentity => $"bios:{Snapshot.BiosVersion?.Trim() ?? "unknown"}";
}
