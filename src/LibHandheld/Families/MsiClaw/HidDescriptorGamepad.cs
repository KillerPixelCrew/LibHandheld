using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.MsiClaw;

/// <summary>
///     Decodes the DirectInput gamepad through its HID report descriptor, the way HC's
///     <c>DClawController</c> reads every Claw through DirectInput's descriptor-parsed state.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ClawControllerCodec" /> decodes fixed byte offsets measured on MS-1T52. Its bits are
///         DirectInput's button indices in order and its bytes the X, Y, Z, Rz, Rx and Ry values, which is
///         the mapping HC applies to every model, but the byte layout itself was only measured on the
///         reference unit. On any other model this class reads the same usages through <c>HidP_*</c>, so
///         a different layout of the same collection still decodes correctly.
///     </para>
///     <para>
///         Mapping, from HC 1.3.1.6 <c>HandheldCompanion.Controllers.MSI/DClawController.cs</c>: button
///         index 0 X, 1 A, 2 B, 3 Y, 4 LB, 5 RB, 8 View, 9 Menu, 10 left stick, 11 right stick, 15 and 16
///         the rear paddles; X/Y the left stick with Y inverted, Z/Rz the right stick with Rz inverted,
///         Rx/Ry the triggers, and the first POV the D-pad. Index 6 and 7 are the triggers' digital
///         bits, which HC reads and WSGM leaves to the analog values. The paddles follow the reference
///         unit's measurement (index 16 left, 15 right), which HC has reversed.
///     </para>
/// </remarks>
internal sealed class HidDescriptorGamepad : IDisposable
{
    private readonly HidUsageReader _reader;

    private HidDescriptorGamepad(HidUsageReader reader)
    {
        _reader = reader;
    }

    public int InputLength => _reader.InputLength;

    public void Dispose()
    {
        _reader.Dispose();
    }

    public static HidDescriptorGamepad Create(SafeFileHandle handle)
    {
        var reader = new HidUsageReader(handle);
        if (!reader.HasValue(0x30) || !reader.HasValue(0x31) || !reader.HasValue(0x32) || !reader.HasValue(0x35))
        {
            reader.Dispose();
            throw new IOException("The gamepad HID descriptor has no X, Y, Z and Rz sticks.");
        }

        return new HidDescriptorGamepad(reader);
    }

    public bool TryDecode(ReadOnlySpan<byte> report, DateTimeOffset timestamp, OemButtonLatch? oemButtons,
        out CanonicalControllerSample sample)
    {
        sample = default;
        if (!_reader.TryValue(report, 0x30, out var x) || !_reader.TryValue(report, 0x31, out var y)
                                                       || !_reader.TryValue(report, 0x32, out var z) ||
                                                       !_reader.TryValue(report, 0x35, out var rz))
        {
            return false;
        }

        var buttons = oemButtons?.Current(timestamp) ?? CanonicalButtons.None;
        foreach (var usage in _reader.Usages(report, 9))
        {
            buttons |= Button(usage - 1);
        }

        if (_reader.TryValue(report, 0x39, out var hat))
        {
            buttons |= HidUsageReader.Hat((int)(hat.Raw - hat.Minimum));
        }

        sample = new CanonicalControllerSample
        {
            Timestamp = timestamp, Buttons = buttons, LeftStickX = x.Signed, LeftStickY = -y.Signed,
            RightStickX = z.Signed, RightStickY = -rz.Signed,
            LeftTrigger = _reader.TryValue(report, 0x33, out var rx) ? rx.Unsigned : 0,
            RightTrigger = _reader.TryValue(report, 0x34, out var ry) ? ry.Unsigned : 0
        };
        return true;
    }

    /// <summary>HC's DirectInput button index to the canonical button.</summary>
    /// <param name="index">Zero-based DirectInput button index.</param>
    /// <returns>The mapped button, or None for unrepresented indices, including digital trigger bits.</returns>
    internal static CanonicalButtons Button(int index)
    {
        return index switch
        {
            0 => CanonicalButtons.X,
            1 => CanonicalButtons.A,
            2 => CanonicalButtons.B,
            3 => CanonicalButtons.Y,
            4 => CanonicalButtons.LeftShoulder,
            5 => CanonicalButtons.RightShoulder,
            8 => CanonicalButtons.View,
            9 => CanonicalButtons.Menu,
            10 => CanonicalButtons.LeftStick,
            11 => CanonicalButtons.RightStick,
            15 => CanonicalButtons.RearPaddle2,
            16 => CanonicalButtons.RearPaddle1,
            _ => CanonicalButtons.None
        };
    }

    /// <summary>A POV value from the descriptor's logical minimum, clockwise from up in eighths.</summary>
    /// <param name="position">Hat value relative to its logical minimum; 0ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“7 run clockwise from up.</param>
    /// <returns>The cardinal/diagonal D-pad mask, or None outside 0ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“7.</returns>
    internal static CanonicalButtons DecodeHat(int position)
    {
        return HidUsageReader.Hat(position);
    }
}
