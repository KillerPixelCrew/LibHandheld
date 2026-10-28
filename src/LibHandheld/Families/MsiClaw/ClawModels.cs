using System.Globalization;
using System.Numerics;
using LibHandheld.Contracts;

namespace LibHandheld.Families.MsiClaw;

/// <summary>
///     One Claw model. Every per-model fact lives in this record; correct a model by changing its row,
///     never by adding a model check elsewhere. Rows other than MS-1T52 are taken from Handheld
///     Companion 1.3.1.6 (<c>_ref/HandheldCompanion</c>) and have no hardware pass; PROVENANCE.md cites each.
/// </summary>
internal sealed record ClawModel
{
    /// <summary>The definition id WSGM keys glyphs and diagnostics on.</summary>
    public required string DefinitionId { get; init; }

    /// <summary>The SMBIOS baseboard product HC's <c>IDevice.GetCurrent</c> switches on.</summary>
    public required string BoardProduct { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>The HC device class the row was read from.</summary>
    public required string HcClass { get; init; }

    /// <summary>True only for a model with an attended hardware pass.</summary>
    public required bool HardwareVerified { get; init; }

    /// <summary>
    ///     The lower bound of both power limits: HC's <c>cTDP[0]</c>, the <c>TDPMin</c> every value HC
    ///     writes is clamped to (<c>PerformanceManager.RequestTDP</c>).
    /// </summary>
    public required int MinimumWatts { get; init; }

    /// <summary>The upper bound of both power limits, HC's <c>cTDP[1]</c>.</summary>
    public required int MaximumWatts { get; init; }

    public required IReadOnlyList<DevicePowerPreset> PowerPresets { get; init; }

    /// <summary>The low bits HC's <c>GetShiftModeValue(User)</c> writes: 3, or 6 on ClawCG3EM.</summary>
    public required byte UserScenario { get; init; }

    /// <summary>
    ///     True where HC's <c>set_short_limit</c> also writes EC 0x52 (ClawBZ2EM, the AMD model, whose
    ///     boost limit is a fast/slow PPT pair).
    /// </summary>
    public required bool WritesFastLimit { get; init; }

    /// <summary>
    ///     True where HC's device JSON declares <c>WindowsGyrometerFields</c> (ClawA2VM, ClawCG3EM): the
    ///     Intel IO driver's "Physical Gyrometer" and "Physical Accelerometer", which HC falls back to
    ///     when WinRT finds no default sensor.
    /// </summary>
    public required bool PhysicalSensorFields { get; init; }

    /// <summary>
    ///     True where the physical sensors are measured to be the right source (MS-1T52): WinRT's default
    ///     gyrometer suppresses unchanged reports there and it projects no accelerometer. Every other
    ///     model follows HC's order, standard sensors first.
    /// </summary>
    public bool PreferPhysicalSensors { get; init; }

    /// <summary>
    ///     Signs applied after HC's shared axis swap (X, Z, Y), from the model's <c>GyroMatrix.Axis</c>.
    /// </summary>
    public required Vector3 GyroSigns { get; init; }

    /// <summary>
    ///     Signs applied after the same swap, from the model's <c>AcceleroMatrix.Axis</c>.
    /// </summary>
    public required Vector3 AccelerometerSigns { get; init; }

    /// <summary>
    ///     True where HC's <c>DClawController</c> drives rumble as on/off at 193 (exactly ClawA1M);
    ///     every later model takes the proportional motor values.
    /// </summary>
    public required bool BinaryRumble { get; init; }

    /// <summary>
    ///     True where the DirectInput report's byte layout was measured (MS-1T52): the fixed-offset
    ///     <see cref="ClawControllerCodec" /> decodes it. Every other model decodes through the HID
    ///     descriptor, as HC's DirectInput path does.
    /// </summary>
    public bool MeasuredControllerReport { get; init; }
}

/// <summary>One row of HC's <c>ClawA1M.deviceVersions</c>: MCU profile addresses for a controller firmware.</summary>
/// <param name="Revision">The MCU revision (USB bcdDevice) the row is for.</param>
/// <param name="Lighting">The RGB profile address.</param>
/// <param name="M1DirectInput">The M1 paddle's DirectInput mapping address.</param>
/// <param name="M2DirectInput">The M2 paddle's DirectInput mapping address.</param>
internal readonly record struct ClawMcuLayout(
    int Revision,
    ushort Lighting,
    ushort M1DirectInput,
    ushort M2DirectInput);

internal static class ClawModels
{
    /// <summary>HC's ClawA1M rumble level, written for any nonzero motor value on that model.</summary>
    public const byte BinaryRumbleLevel = 193;

    /// <summary>HC's ClawA1M rumble thread interval: at most one motor write per 100 ms.</summary>
    public static readonly TimeSpan BinaryRumbleInterval = TimeSpan.FromMilliseconds(100);

    private static readonly Vector3 Positive = new(1, 1, 1);
    private static readonly Vector3 FlippedZ = new(1, 1, -1);

    /// <summary>
    ///     HC's <c>deviceVersions</c>, chosen by the revision nearest the one reported
    ///     (<c>FirmwareDevice</c> is a MinBy on the absolute difference, the first row winning a tie).
    ///     Revisions 0x0163 and 0x0211 use the older addresses; 0x0166, 0x0167 and 0x0217 onwards the
    ///     newer ones, where the reference unit measured lighting on 0229 and 0230.
    /// </summary>
    private static readonly ClawMcuLayout[] McuLayouts =
    [
        new(0x0163, 0x01FA, 0x007A, 0x011F), new(0x0166, 0x024A, 0x00BA, 0x0163),
        new(0x0167, 0x024A, 0x00BA, 0x0163), new(0x0211, 0x01FA, 0x007A, 0x011F),
        new(0x0217, 0x024A, 0x00BA, 0x0163), new(0x0219, 0x024A, 0x00BA, 0x0163),
        new(0x0308, 0x024A, 0x00BA, 0x0163), new(0x0411, 0x024A, 0x00BA, 0x0163),
        new(0x0414, 0x024A, 0x00BA, 0x0163)
    ];

    public static ClawModel A1M { get; } = new()
    {
        DefinitionId = "ms-1t41",
        BoardProduct = "MS-1T41",
        DisplayName = "MSI Claw A1M",
        HcClass = "ClawA1M",
        HardwareVerified = false,
        MinimumWatts = 20,
        MaximumWatts = 45,
        PowerPresets = Presets((20, 20), (30, 30), (35, 35), (45, 45)),
        UserScenario = 3,
        WritesFastLimit = false,
        PhysicalSensorFields = false,
        GyroSigns = FlippedZ,
        AccelerometerSigns = new Vector3(-1, -1, 1),
        BinaryRumble = true
    };

    public static ClawModel Claw7A2Vm { get; } = new()
    {
        DefinitionId = "ms-1t42",
        BoardProduct = "MS-1T42",
        DisplayName = "MSI Claw 7 AI+ A2VM",
        HcClass = "ClawA2VM",
        HardwareVerified = false,
        MinimumWatts = 8,
        MaximumWatts = 37,
        PowerPresets = Presets((8, 9), (17, 18), (30, 31), (37, 37)),
        UserScenario = 3,
        WritesFastLimit = false,
        PhysicalSensorFields = true,
        GyroSigns = FlippedZ,
        AccelerometerSigns = FlippedZ,
        BinaryRumble = false
    };

    /// <summary>The reference unit. Its power range and presets are the ones measured and reviewed there.</summary>
    public static ClawModel Claw8A2Vm { get; } = Claw7A2Vm with
    {
        DefinitionId = "ms-1t52",
        BoardProduct = "MS-1T52",
        DisplayName = "MSI Claw 8 AI+ A2VM",
        HardwareVerified = true,
        PreferPhysicalSensors = true,
        MeasuredControllerReport = true
    };

    public static ClawModel A8Bz2Em { get; } = new()
    {
        DefinitionId = "ms-1t8k",
        BoardProduct = "MS-1T8K",
        DisplayName = "MSI Claw A8 BZ2EM",
        HcClass = "ClawBZ2EM",
        HardwareVerified = false,
        MinimumWatts = 15,
        MaximumWatts = 35,
        PowerPresets = Presets((15, 15), (20, 20), (28, 28), (35, 35)),
        UserScenario = 3,
        WritesFastLimit = true,
        PhysicalSensorFields = false,
        GyroSigns = FlippedZ,
        AccelerometerSigns = Positive,
        BinaryRumble = false
    };

    public static ClawModel Claw8ExCg3Em { get; } = new()
    {
        DefinitionId = "ms-1t91",
        BoardProduct = "MS-1T91",
        DisplayName = "MSI Claw 8 EX AI+ CG3EM",
        HcClass = "ClawCG3EM",
        HardwareVerified = false,
        // HC declares a {15, 15, 20} Better Battery override, but clamps every value to cTDP 20-37
        // before writing, so what reaches the EC is 20/20.
        MinimumWatts = 20,
        MaximumWatts = 37,
        PowerPresets = Presets((20, 20), (25, 37), (30, 37), (37, 37)),
        UserScenario = 6,
        WritesFastLimit = false,
        PhysicalSensorFields = true,
        GyroSigns = FlippedZ,
        AccelerometerSigns = FlippedZ,
        BinaryRumble = false
    };

    public static IReadOnlyList<ClawModel> All { get; } = [A1M, Claw7A2Vm, Claw8A2Vm, A8Bz2Em, Claw8ExCg3Em];

    /// <summary>
    ///     Matches HC's identity switch: the baseboard manufacturer (Win32_BaseBoard, upper-cased by HC)
    ///     and the exact baseboard product, the same fields the manifest's hardware rules use. The SKU
    ///     is recorded, never matched, because HC does not read it and it is unknown for every model but
    ///     the reference unit.
    /// </summary>
    /// <param name="identity">Non-null side-effect-free SMBIOS/baseboard snapshot.</param>
    /// <returns>The catalog model matching trimmed manufacturer/product case-insensitively, or null for another machine.</returns>
    public static ClawModel? Find(DeviceIdentitySnapshot identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.BaseboardManufacturer?.Trim(), ClawHardwareFacts.Manufacturer,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var board = identity.BaseboardProduct?.Trim();
        return All.FirstOrDefault(model =>
            string.Equals(model.BoardProduct, board, StringComparison.OrdinalIgnoreCase));
    }

    public static ClawModel? FindByDefinition(string? definitionId)
    {
        return All.FirstOrDefault(model => string.Equals(model.DefinitionId, definitionId, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The MCU layout for a revision (USB bcdDevice as hex, "0230"). A revision that cannot be read
    ///     takes the reference unit's measured layout; HC would take its nearest-to-zero row instead.
    /// </summary>
    /// <param name="mcuRevision">USB bcdDevice hexadecimal text, or null when unavailable.</param>
    /// <returns>The nearest numeric revision row; invalid or absent text uses the last measured layout.</returns>
    public static ClawMcuLayout McuLayout(string? mcuRevision)
    {
        if (!int.TryParse(mcuRevision, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var revision))
        {
            return McuLayouts[^1];
        }

        return McuLayouts.MinBy(row => Math.Abs(row.Revision - revision));
    }

    /// <summary>The RGB profile address for an MCU revision; see <see cref="McuLayout" />.</summary>
    /// <param name="mcuRevision">USB bcdDevice hexadecimal text, or null for the measured fallback.</param>
    /// <returns>The lighting address from the selected nearest-revision MCU layout.</returns>
    public static ushort LightingProfileAddress(string? mcuRevision)
    {
        return McuLayout(mcuRevision).Lighting;
    }

    /// <summary>HC's three device power profiles, in order, and WSGM's Full Power at the model ceiling.</summary>
    /// <remarks>
    ///     HC's TDP override arrays are {PL1, PL1, PL2}; the sustained/slow pair takes the first and last,
    ///     after HC's clamp to <c>cTDP</c>. The inherited ClawA1M profile handler selects Eco/Green/Sport
    ///     on AC and Comfort on DC.
    /// </remarks>
    private static IReadOnlyList<DevicePowerPreset> Presets(
        (int Sustained, int Slow) battery,
        (int Sustained, int Slow) balanced,
        (int Sustained, int Slow) performance,
        (int Sustained, int Slow) full)
    {
        return
        [
            new DevicePowerPreset("super-battery", "Super Battery", battery.Sustained, battery.Slow,
                DevicePowerMode.BetterBattery) { ScenarioOnAc = "eco", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("balanced", "Balanced", balanced.Sustained, balanced.Slow,
                DevicePowerMode.Balanced) { ScenarioOnAc = "green", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("extreme-performance", "Extreme Performance", performance.Sustained,
                performance.Slow, DevicePowerMode.BestPerformance) { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" },
            new DevicePowerPreset("full-power", "Full Power", full.Sustained, full.Slow,
                DevicePowerMode.BestPerformance) { ScenarioOnAc = "sport", ScenarioOnDc = "comfort" }
        ];
    }
}
