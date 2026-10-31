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
    /// <summary>HC's DirectInput button indices; 6 and 7 are the triggers' digital bits.</summary>
    private static readonly CanonicalButtons[] Buttons =
    [
        CanonicalButtons.X,
        CanonicalButtons.A,
        CanonicalButtons.B,
        CanonicalButtons.Y,
        CanonicalButtons.LeftShoulder,
        CanonicalButtons.RightShoulder,
        CanonicalButtons.None,
        CanonicalButtons.None,
        CanonicalButtons.View,
        CanonicalButtons.Menu,
        CanonicalButtons.LeftStick,
        CanonicalButtons.RightStick,
        CanonicalButtons.None,
        CanonicalButtons.None,
        CanonicalButtons.None,
        CanonicalButtons.RearPaddle2,
        CanonicalButtons.RearPaddle1
    ];

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
        return _reader.TryDecodeGamepad(report, timestamp, Buttons,
            oemButtons?.Current(timestamp) ?? CanonicalButtons.None, out sample);
    }

    /// <summary>HC's DirectInput button index to the canonical button.</summary>
    /// <param name="index">Zero-based DirectInput button index.</param>
    /// <returns>The mapped button, or None for unrepresented indices, including digital trigger bits.</returns>
    internal static CanonicalButtons Button(int index)
    {
        return (uint)index < (uint)Buttons.Length ? Buttons[index] : CanonicalButtons.None;
    }
}
