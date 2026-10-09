using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.LegionGo;

/// <summary>Windows descriptor-decoded Legion DirectInput state; no assumed input-report byte layout.</summary>
internal sealed class LegionDInput : IDisposable
{
    private readonly HidUsageReader _reader;

    public LegionDInput(SafeFileHandle handle)
    {
        _reader = new HidUsageReader(handle);
    }

    public void Dispose()
    {
        _reader.Dispose();
    }

    public bool TryDecode(ReadOnlySpan<byte> report, DateTimeOffset timestamp, out CanonicalControllerSample sample)
    {
        sample = default;
        if (!_reader.TryValue(report, 0x30, out var x) || !_reader.TryValue(report, 0x31, out var y)
                                                       || !_reader.TryValue(report, 0x32, out var z) ||
                                                       !_reader.TryValue(report, 0x35, out var rz))
        {
            return false;
        }

        var buttons = CanonicalButtons.None;
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

    internal static CanonicalButtons Button(int index)
    {
        return index switch
        {
            0 => CanonicalButtons.A, 1 => CanonicalButtons.B, 3 => CanonicalButtons.X, 4 => CanonicalButtons.Y,
            6 => CanonicalButtons.LeftShoulder, 7 => CanonicalButtons.RightShoulder,
            10 => CanonicalButtons.View, 11 => CanonicalButtons.Menu,
            13 => CanonicalButtons.LeftStick, 14 => CanonicalButtons.RightStick, _ => 0
        };
    }
}
