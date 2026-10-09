using LibHandheld.Families.Generic;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Identity;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using HandheldDefinition = LibHandheld.Contracts.HandheldDefinition;
using DeviceHardwareAccess = LibHandheld.Contracts.DeviceHardwareAccess;

namespace LibHandheld.Families.Zotac;

internal static class ZotacModels
{
    internal const string FamilyId = "zotac";

    private static readonly ProfiledDeviceModel Zone = new()
    {
        Definition = new HandheldDefinition("zotac-zone", FamilyId, "ZOTAC GAMING ZONE", "zotac-zone")
            { HardwareAccess = DeviceHardwareAccess.PawnIo },
        SourceFamilyId = "GamingZone", Manufacturers = ["PC PARTNER LIMITED", "ZOTAC"], Products = ["G0A1W"],
        Cpu = CpuPowerKind.Amd, MinimumWatts = 8, MaximumWatts = 30,
        Fan = new EcFanProfile(0x4E, 0x4F, [0x044B], 255, 0x044A),
        Controller = new StandardControllerProfile([new ControllerUsbId(0x1EE9, 0x1590)]),
        Motion = new StandardMotionProfile(new MotionAxisMap(0, 2, 1, 1, 1, -1), new MotionAxisMap(0, 2, 1, 1, 1, 1)),
        KeyboardChords =
        [
            new KeyboardOemChord("zone", "ZONE", [0xA2, 0x5B, 0x80], CanonicalButtons.Guide),
            new KeyboardOemChord("dots", "Dots", [0xA2, 0x5B, 0x81], CanonicalButtons.QuickAccess),
            new KeyboardOemChord("home", "Home", [0x5B, 0x44], CanonicalButtons.None),
            new KeyboardOemChord("m1", "M1", [0xA2, 0x5B, 0x7A], CanonicalButtons.RearPaddle1),
            new KeyboardOemChord("m2", "M2", [0xA2, 0x5B, 0x7B], CanonicalButtons.RearPaddle2)
        ],
        CreateOemSource = () => new ZotacWheelSource(), NativeOemReplacesChords = [],
        OemControls = ZotacWheelSource.Controls,
        CreateNativeService = context => new ZotacNativeService(context)
    };

    // HHD selects the system product, HC the baseboard. Both represent the same physical model.
    private static readonly ProfiledDeviceModel HhdZone = Zone with
    {
        IdentitySource = ProfileIdentitySource.System, Products = ["ZOTAC GAMING ZONE"]
    };

    internal static IReadOnlyList<HandheldDefinition> GetDefinitions()
    {
        return [Zone.Definition];
    }

    internal static ProfiledDeviceModel? Match(DeviceIdentitySnapshot identity)
    {
        return Zone.Matches(identity)
            ? Zone
            : string.Equals(identity.SystemProduct, "ZOTAC GAMING ZONE", StringComparison.OrdinalIgnoreCase)
                ? HhdZone with
                {
                    Manufacturers = [identity.SystemManufacturer ?? identity.BaseboardManufacturer ?? ""],
                    Processors = [identity.ProcessorName ?? ""]
                }
                : null;
    }

    internal static IDeviceEngine CreateEngine(HandheldDefinition definition)
    {
        return new ZotacDeviceEngine(definition);
    }

    private sealed class ZotacDeviceEngine(HandheldDefinition definition) : ProfiledEngineSelector(definition)
    {
        protected override ProfiledDeviceModel? Select(DeviceIdentitySnapshot identity)
        {
            return Match(identity);
        }
    }
}
