using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Acer;

/// <summary>A lighting target the controller's FF5A collection lists, with its A3 capabilities.</summary>
/// <param name="Id">The target byte that A2, A3 and A4 address.</param>
/// <param name="LedCount">The LEDs addressable through the A4 mask.</param>
/// <param name="MaxSpeed">The top effect speed the target declares.</param>
internal readonly record struct AcerRingTarget(byte Id, int LedCount, int MaxSpeed);

/// <summary>The Atlas 7's ring LEDs, which answer on the controller's FF5A collection and not to RAM profile writes.</summary>
/// <remarks>
///     Uses the A1..A4 feature reports of the ENE rear logo, on <c>0502:F310</c>. A steady color is one A4
///     per LED with a one-bit mask and a 20 ms gap; an animated effect is one A4 per target with mask 0.
///     Both follow Predator Sense's <c>AcerHIDStickController.dll</c>. A4 is write-only, so the colors and
///     brightness come from the controller section <see cref="AcerLightingService" /> retains.
/// </remarks>
internal sealed class AcerRingLights
{
    internal const int ReportLength = 11;
    private const byte ListReport = 0xA1;
    private const byte SelectReport = 0xA2;
    private const byte CapabilityReport = 0xA3;
    private const byte SetReport = 0xA4;
    private const byte SteadyMode = 2;
    private const byte BreathingMode = 4;
    private const int DefaultLedCount = 8;
    private const int MaskBits = 16;
    private const int NinthSlotCount = 9;
    private const int UiSpeedRange = 20;
    private const string Scope = "acer-lighting";
    private static readonly TimeSpan WriteGap = TimeSpan.FromMilliseconds(20);

    private SafeFileHandle? _handle;
    private IReadOnlyList<AcerRingTarget> _targets = [];

    internal static bool IsCollection(HidCollection item)
    {
        return item is { ProductId: AcerControllerTransport.Atlas7ProductId, UsagePage: 0xFF5A, Usage: 1,
            ReleaseNumber: 0x0100, FeatureLength: ReportLength };
    }

    /// <summary>Reads an A1 reply: a count, then that many target bytes.</summary>
    internal static byte[] ParseTargets(ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength || report[0] != ListReport || report[1] == 0 || report[1] > ReportLength - 2)
        {
            throw new IOException("The ring lighting interface returned an unusable target list.");
        }

        return report.Slice(2, report[1]).ToArray();
    }

    /// <summary>Reads an A3 reply for a target: top speed at byte 3 and LED count at byte 4.</summary>
    internal static AcerRingTarget ParseCapability(byte target, ReadOnlySpan<byte> report)
    {
        if (report.Length != ReportLength || report[0] != CapabilityReport || report[1] != target)
        {
            throw new IOException($"The ring lighting interface did not describe target {target:X2}.");
        }

        return new AcerRingTarget(target, report[4] is >= 1 and <= MaskBits ? report[4] : DefaultLedCount, report[3]);
    }

    /// <summary>Encodes an 11-byte A4 report in the field order of the rear logo's.</summary>
    internal static byte[] Packet(byte target, byte mode, int brightness, int speed, int color, ushort mask)
    {
        return [SetReport, target, mode, checked((byte)brightness), checked((byte)speed), 0,
            (byte)(color >> 16), (byte)(color >> 8), (byte)color, (byte)mask, (byte)(mask >> 8)];
    }

    /// <summary>The writes that show the section's selected animation.</summary>
    /// <remarks>Breathing is one write per target. Everything else shows its first keyframe, LED by LED.</remarks>
    internal static IReadOnlyList<byte[]> Frames(IReadOnlyList<AcerRingTarget> targets, byte[] lighting)
    {
        var offset = AcerLightingService.AnimationOffset(lighting);
        var brightness = lighting[offset + 3];
        var colors = lighting.AsSpan(offset + 4, NinthSlotCount * 3);
        List<byte[]> frames = [];
        if (lighting[offset] == 2)
        {
            var uiSpeed = UiSpeedRange - lighting[offset + 2];
            foreach (var target in targets)
            {
                var speed = target.MaxSpeed > 0 ? (uiSpeed * target.MaxSpeed + UiSpeedRange / 2) / UiSpeedRange : uiSpeed;
                frames.Add(Packet(target.Id, BreathingMode, brightness, speed, Color(colors), 0));
            }

            return frames;
        }

        var slot = 0;
        foreach (var target in targets)
        {
            for (var bit = 0; bit < target.LedCount; bit++, slot++)
            {
                var rgb = slot < NinthSlotCount ? colors.Slice(slot * 3, 3) : colors[..3];
                frames.Add(Packet(target.Id, SteadyMode, brightness, 0, Color(rgb), (ushort)(1 << bit)));
            }
        }

        return frames;
    }


    private static int Color(ReadOnlySpan<byte> rgb)
    {
        return (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
    }

    /// <summary>Opens the collection and queries its targets, logging the raw replies.</summary>
    /// <returns>Whether at least one target can be written.</returns>
    internal bool Open()
    {
        Close();
        var collection = HidDevices.Unique(HidDevices.Enumerate(0x0502, [AcerControllerTransport.Atlas7ProductId]), IsCollection);
        if (collection is null)
        {
            DeviceTrace.Warn(Scope, "The Atlas 7 ring lighting collection FF5A:0001 is unavailable.");
            return false;
        }

        _handle = HidDevices.Open(collection, overlapped: false);
        try
        {
            var list = new byte[ReportLength];
            list[0] = ListReport;
            Get(list);
            DeviceTrace.Info(Scope, $"Ring lighting {collection.Describe()} A1 {Convert.ToHexString(list)}");
            List<AcerRingTarget> targets = [];
            foreach (var id in ParseTargets(list))
            {
                targets.Add(Query(id));
            }

            _targets = targets;
            return true;
        }
        catch
        {
            Close();
            throw;
        }
    }

    private AcerRingTarget Query(byte id)
    {
        var select = new byte[ReportLength];
        select[0] = SelectReport;
        select[1] = id;
        Set(select);
        Thread.Sleep(WriteGap);
        var capability = new byte[ReportLength];
        capability[0] = CapabilityReport;
        Get(capability);
        var target = ParseCapability(id, capability);
        DeviceTrace.Info(Scope, $"Ring lighting target {id:X2} A3 {Convert.ToHexString(capability)} leds {target.LedCount}");
        return target;
    }

    /// <summary>Shows the section's selected animation on the rings.</summary>
    internal ValueTask ShowAsync(byte[] lighting, CancellationToken token)
    {
        return WriteAsync(Frames(RequireTargets(), lighting), token);
    }


    private IReadOnlyList<AcerRingTarget> RequireTargets()
    {
        return _handle is not null && _targets.Count > 0
            ? _targets
            : throw new IOException("The Atlas 7 ring lighting interface is unavailable.");
    }

    private async ValueTask WriteAsync(IReadOnlyList<byte[]> frames, CancellationToken token)
    {
        foreach (var frame in frames)
        {
            token.ThrowIfCancellationRequested();
            Set(frame);
            await Task.Delay(WriteGap, token).ConfigureAwait(false);
        }
    }

    private void Get(byte[] report)
    {
        if (!NativeHid.HidD_GetFeature(_handle!, report, report.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Reading ring lighting report {report[0]:X2} failed.");
        }
    }

    private void Set(byte[] report)
    {
        if (!NativeHid.HidD_SetFeature(_handle!, report, report.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Writing ring lighting report {report[0]:X2} failed.");
        }
    }

    internal void Close()
    {
        _handle?.Dispose();
        _handle = null;
        _targets = [];
    }
}
