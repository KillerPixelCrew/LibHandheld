using System.ComponentModel;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.Asus;

/// <summary>Battery charge ceiling over ATKACPI.</summary>
/// <remarks>HC writes 0-100 through DEVS 0x00120057 (<c>AsusACPI.cs:335-341</c>).</remarks>
/// <param name="acpi">Shared ATKACPI transport for the persistent charge-limit setting.</param>
internal sealed class AllyChargeLimitCapability(IAsusAcpi acpi)
{
    /// <summary>
    ///     HC AsusACPI.SetChargeLimit accepts the complete 0-100 percent range.
    /// </summary>
    internal const int MinimumPercent = 0;

    internal const int MaximumPercent = 100;

    private readonly IAsusAcpi _acpi = acpi ?? throw new ArgumentNullException(nameof(acpi));

    public int? Read()
    {
        return AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(AsusAcpiId.ChargeLimit), out var value)
               && value is >= 0 and <= 100
            ? value
            : null;
    }

    public CapabilityCommandResult Apply(CapabilityCommand command, int percent)
    {
        if (percent is < MinimumPercent or > MaximumPercent)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                $"The charge limit must be {MinimumPercent}-{MaximumPercent} %.");
        }

        try
        {
            _ = _acpi.Write(AsusAcpiId.ChargeLimit, (uint)percent);
            return CommandResults.Applied(command);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            DeviceTrace.Failure("charge-limit", "Charge limit write failed", ex);
            return CommandResults.TransportFailure(command, dispatched: true, ex);
        }
    }
}

/// <summary>The eight-point ATKACPI fan-curve contract shared by every ASUS fan channel.</summary>
/// <remarks>
///     HC converts its curve to sixteen bytes, eight temperatures then eight duties, and clamps duties to
///     99 (<c>ROGAlly.cs:287-327</c>, <c>AsusACPI.cs:281-298</c>).
/// </remarks>
internal static class AsusFanCurve
{
    internal const int PointCount = 8;
    internal const int MinimumTemperature = 20;
    internal const int MaximumTemperature = 110;

    /// <summary>HC's <c>defaultCPUFan</c>, also written to the mid fan (<c>ROGAlly.cs:185-189</c>).</summary>
    internal static readonly byte[] DefaultCpuCurve = [58, 61, 64, 68, 72, 77, 81, 98, 8, 17, 22, 26, 34, 41, 48, 69];

    /// <summary>HC's <c>defaultGPUFan</c> (<c>ROGAlly.cs:191-195</c>).</summary>
    internal static readonly byte[] DefaultGpuCurve = [58, 61, 64, 68, 72, 77, 81, 98, 12, 22, 29, 31, 38, 45, 52, 74];

    public static IReadOnlyList<CurvePoint> Decode(byte[] curve)
    {
        return [.. Enumerable.Range(0, PointCount).Select(index => new CurvePoint(curve[index], curve[8 + index]))];
    }

    /// <summary>Encodes eight semantic points as the firmware's sixteen bytes.</summary>
    /// <param name="points">Exactly eight increasing temperatures with nondecreasing duties, within the admitted ranges.</param>
    /// <param name="curve">Sixteen encoded bytes on success; ignore the allocated or partially filled array on failure.</param>
    /// <param name="error">Null on success, otherwise the validation message.</param>
    /// <returns>Whether the points form a valid curve with at least one nonzero duty; 100-percent duties are clamped to 99.</returns>
    public static bool TryEncode(IReadOnlyList<CurvePoint> points, out byte[] curve, out string? error)
    {
        curve = new byte[AsusAcpiProtocol.CurveLength];
        if (points.Count != PointCount)
        {
            error = "ASUS fan curves have exactly eight points.";
            return false;
        }

        for (var index = 0; index < PointCount; index++)
        {
            var point = points[index];
            if (point.Input is < MinimumTemperature or > MaximumTemperature || point.Output is < 0 or > 100)
            {
                error = $"Fan points must be {MinimumTemperature}-{MaximumTemperature} °C and 0-100 %.";
                return false;
            }

            if (index > 0 && (point.Input <= points[index - 1].Input || point.Output < points[index - 1].Output))
            {
                error = "Fan-curve temperatures must rise and duties must not fall.";
                return false;
            }

            curve[index] = (byte)point.Input;
            // HC clamps every duty to 99 before writing (AsusACPI.SetFanCurve).
            curve[8 + index] = (byte)Math.Min(point.Output, 99);
        }

        if (!AsusAcpiProtocol.IsValidCurve(curve))
        {
            error = "The fan curve needs at least one non-zero duty.";
            return false;
        }

        error = null;
        return true;
    }
}
