using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

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

/// <summary>Supplies read-only identity for model admission and recovery binding.</summary>
internal interface IAllyIdentityReader
{
    /// <summary>Reads the Windows SMBIOS registry projection and current AC-line status.</summary>
    /// <param name="cancellationToken">Cancels before synchronous registry and power-status reads.</param>
    /// <returns>Normalized snapshot and matched model; a missing match remains null, and unknown AC status means AC.</returns>
    ValueTask<AllyIdentityState> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads SMBIOS identity from the registry copy Windows keeps, and the power source.</summary>
/// <remarks>
///     <c>HKLM\HARDWARE\DESCRIPTION\System\BIOS</c> is populated from SMBIOS at boot, so reading it
///     touches no device and needs no WMI provider. HC matches the same baseboard fields through
///     <c>MotherboardInfo</c> (<c>HandheldCompanion.Devices/IDevice.cs:710-716</c>).
/// </remarks>
internal sealed class WindowsAllyIdentityReader : IAllyIdentityReader
{
    /// <inheritdoc />
    public ValueTask<AllyIdentityState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = WindowsMachineIdentity.ReadSmbios();
        return ValueTask.FromResult(new AllyIdentityState
        {
            Snapshot = snapshot,
            Model = AllyModels.Match(snapshot),
            OnAcPower = WindowsMachineIdentity.OnAcPower()
        });
    }
}
