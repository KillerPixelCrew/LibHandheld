using System.Numerics;
using LibHandheld.Contracts;
using LibHandheld.Internal.Transports;
using LibHandheld.Internal.Windows;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;

namespace LibHandheld.Internal.Devices;

/// <summary>Facts that change between models sharing the standard Windows controller and CPU transports.</summary>
internal sealed record ProfiledDeviceModel
{
    internal required HandheldDefinition Definition { get; init; }
    internal IReadOnlyList<CapabilityRole> NativeRoles { get; init; } = [];
    internal bool UseStandardServices { get; init; } = true;
    internal IReadOnlyList<Func<ProfiledFeatureContext, ProfiledFeatureService>> ServiceFactories { get; init; } = [];
    internal Func<ProfiledFeatureContext, ICpuPowerTransport>? OpenPower { get; init; }
    internal bool IndependentPowerLimits => UseStandardServices && Cpu == CpuPowerKind.Amd && OpenPower is null;
    internal IReadOnlyList<DevicePowerPreset> PowerPresets { get; init; } = [];

    internal HandheldDefinition DeclaredDefinition
    {
        get
        {
            var roles = new List<CapabilityRole>();
            if (UseStandardServices)
            {
                roles.AddRange([CapabilityRole.PowerSustainedLimit, CapabilityRole.PowerSlowLimit]);
                if (IndependentPowerLimits)
                {
                    roles.Add(CapabilityRole.PowerFastLimit);
                }

                if (Cpu == CpuPowerKind.Intel)
                {
                    roles.Add(CapabilityRole.PowerBackend);
                }
            }

            if (Fan is not null)
            {
                roles.AddRange([CapabilityRole.FanMode, CapabilityRole.FanDuty]);
                if (Fan.RpmHighAddress is not null)
                {
                    roles.Add(CapabilityRole.FanMeasuredRpm);
                }
            }

            if (Controller is not null)
            {
                roles.AddRange([CapabilityRole.ControllerSource, CapabilityRole.HapticSink]);
            }

            if (Motion is not null)
            {
                roles.Add(CapabilityRole.MotionSource);
            }

            roles.AddRange(NativeRoles);
            return Definition with { DeclaredRoles = roles };
        }
    }

    internal required string SourceFamilyId { get; init; }
    internal required IReadOnlyList<string> Manufacturers { get; init; }
    internal required IReadOnlyList<string> Products { get; init; }
    internal IReadOnlyList<string> SystemProducts { get; init; } = [];
    internal IReadOnlyList<string> Processors { get; init; } = [];
    internal IReadOnlyList<string> ProcessorSkus { get; init; } = [];
    internal IReadOnlyList<string> Versions { get; init; } = [];
    internal ProfileIdentitySource IdentitySource { get; init; } = ProfileIdentitySource.Baseboard;
    internal CpuPowerKind Cpu { get; init; }
    internal required int MinimumWatts { get; init; }
    internal required int MaximumWatts { get; init; }
    internal EcFanProfile? Fan { get; init; }
    internal StandardControllerProfile? Controller { get; init; }
    internal StandardMotionProfile? Motion { get; init; }
    internal StandardTouchProfile? Touchpad { get; init; }
    internal IReadOnlyList<KeyboardOemChord> KeyboardChords { get; init; } = [];
    internal IReadOnlyList<OemControlDescriptor> OemControls { get; init; } = [];
    internal Func<IProfiledOemSource>? CreateOemSource { get; init; }
    internal IReadOnlyList<string> NativeOemReplacesChords { get; init; } = [];
    internal bool NativeOemRequiresController { get; init; } = true;

    internal static ProfiledDeviceModel ForServices(HandheldDefinition definition,
        IReadOnlyList<Func<ProfiledFeatureContext, ProfiledFeatureService>> factories)
    {
        return new ProfiledDeviceModel
        {
            Definition = definition, SourceFamilyId = definition.FamilyId, Manufacturers = [], Products = [],
            MinimumWatts = 0, MaximumWatts = 0, UseStandardServices = false,
            NativeRoles = definition.DeclaredRoles, ServiceFactories = factories
        };
    }

    internal bool Matches(DeviceIdentitySnapshot identity)
    {
        var manufacturer = IdentitySource == ProfileIdentitySource.Baseboard
            ? identity.BaseboardManufacturer ?? identity.SystemManufacturer
            : identity.SystemManufacturer ?? identity.BaseboardManufacturer;
        var product = IdentitySource == ProfileIdentitySource.Baseboard
            ? identity.BaseboardProduct ?? identity.SystemProduct
            : identity.SystemProduct ?? identity.BaseboardProduct;
        return Manufacturers.Any(value => Equal(value, manufacturer))
               && (Products.Any(value => Equal(value, product))
                   || SystemProducts.Any(value => Equal(value, identity.SystemProduct)))
               && (Versions.Count == 0 || Versions.Any(value => Equal(value, identity.BaseboardVersion)))
               && ((Processors.Count == 0 && ProcessorSkus.Count == 0)
                   || Processors.Any(value => Equal(value, identity.ProcessorName))
                   || ProcessorSkus.Any(value => HasSku(identity.ProcessorName, value)));
    }

    private static bool Equal(string expected, string? actual)
    {
        return string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSku(string? processor, string sku)
    {
        if (string.IsNullOrWhiteSpace(processor) || string.IsNullOrWhiteSpace(sku))
        {
            return false;
        }

        for (var start = 0; start < processor.Length;)
        {
            var index = processor.IndexOf(sku, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            var end = index + sku.Length;
            if ((index == 0 || !char.IsLetterOrDigit(processor[index - 1]))
                && (end == processor.Length || !char.IsLetterOrDigit(processor[end])))
            {
                return true;
            }

            start = end;
        }

        return false;
    }
}

internal enum CpuPowerKind
{
    Amd,
    Intel
}

internal enum ProfileIdentitySource
{
    Baseboard,
    System
}

/// <summary>The EC register codec is data; acquisition, journalling and restoration are shared.</summary>
internal sealed record EcFanProfile(
    ushort IndexPort,
    ushort DataPort,
    IReadOnlyList<ushort> DutyAddresses,
    byte MaximumDuty,
    ushort? ManualAddress = null,
    byte ManualValue = 1,
    byte AutomaticValue = 0,
    ushort? UnlockChipAddress = null,
    byte UnlockChipValue = 0x55,
    ushort? UnlockAddress = null,
    byte UnlockMask = 0x80,
    ushort? RpmHighAddress = null,
    ushort? RpmLowAddress = null,
    bool ReadDuty = true,
    bool ObserveDuty = false,
    ushort? AutomaticDutyAddress = null)
{
    internal byte EncodeDuty(int percentage)
    {
        return checked((byte)Math.Round(Math.Clamp(percentage, 0, 100) * MaximumDuty / 100d));
    }
}

internal sealed record StandardControllerProfile(IReadOnlyList<ControllerUsbId> UsbIds)
{
    internal HapticCapabilities Haptics { get; init; } = new()
    {
        LowFrequency = OutputChannelSupport.Native,
        HighFrequency = OutputChannelSupport.Native
    };

    internal HidControllerProfile? HidFallback { get; init; }
    internal Func<HidCollection, IHidControllerRumble>? OpenHidRumble { get; init; }
    /// <summary>Measured companion identities required by the selected HID topology; the host owns hiding.</summary>
    internal Func<HidCollection, IReadOnlyList<PhysicalDeviceIdentity>>? HidCompanions { get; init; }
    internal IReadOnlyList<ControllerUsbId> CompanionUsbIds { get; init; } = [];
    /// <summary>
    ///     Exact instance-path fragments of auxiliary HID leaves that clients list as extra pads, each
    ///     verified in an attended keyboard trial. The host hides them with the pad.
    /// </summary>
    internal IReadOnlyList<string> AuxiliaryHidCollections { get; init; } = [];

    /// <summary>
    ///     Whether a reader left blocked in XInput for three seconds cycles the pad's USB port, once per
    ///     reader. Only for a pad measured to hang that way while staying enumerated.
    /// </summary>
    internal bool CycleUsbPortWhenStalled { get; init; }
}

internal sealed record HidControllerProfile(
    IReadOnlyList<ControllerUsbId> UsbIds,
    IReadOnlyList<CanonicalButtons> Buttons,
    ushort LeftX = 0x30,
    ushort LeftY = 0x31,
    ushort RightX = 0x32,
    ushort RightY = 0x35,
    ushort LeftTrigger = 0x33,
    ushort RightTrigger = 0x34,
    ushort LeftTriggerButton = 7,
    ushort RightTriggerButton = 8);

internal sealed record StandardTouchProfile(IReadOnlyList<ControllerUsbId> UsbIds);

internal readonly record struct ControllerUsbId(ushort Vendor, ushort Product);

internal readonly record struct MotionAxisMap(int X, int Y, int Z, float SignX, float SignY, float SignZ)
{
    // The constructor selects one input for each output. HC instead stores the output
    // destination of each input; its signs still belong to the output axes.
    internal static MotionAxisMap FromInputToOutput(int x, int y, int z, float signX, float signY, float signZ)
    {
        if (x is < 0 or > 2 || y is < 0 or > 2 || z is < 0 or > 2 || x == y || x == z || y == z)
        {
            throw new ArgumentException("A motion axis mapping must contain each output axis exactly once.");
        }

        return new MotionAxisMap(
            x == 0 ? 0 : y == 0 ? 1 : 2,
            x == 1 ? 0 : y == 1 ? 1 : 2,
            x == 2 ? 0 : y == 2 ? 1 : 2,
            signX, signY, signZ);
    }

    internal Vector3 Apply(Vector3 value)
    {
        return new Vector3(
            Pick(value, X) * SignX, Pick(value, Y) * SignY, Pick(value, Z) * SignZ);
    }

    private static float Pick(Vector3 value, int axis)
    {
        return axis switch { -1 => 0, 0 => value.X, 1 => value.Y, _ => value.Z };
    }
}

internal sealed record StandardMotionProfile(MotionAxisMap Gyro, MotionAxisMap Acceleration);

internal sealed record KeyboardOemChord(
    string Id,
    string Label,
    IReadOnlyList<uint> Keys,
    CanonicalButtons Button,
    bool RequiresController = true);

internal interface IProfiledOemSource : IAsyncDisposable
{
    bool IsConnected => true;
    ValueTask<bool> StartAsync(Action<string, CanonicalButtons, bool> publish, CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}
