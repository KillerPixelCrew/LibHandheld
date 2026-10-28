using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Contracts;
using LibHandheld.Internal.Devices;

namespace LibHandheld.Internal.Windows;

internal sealed record StandardControllerTopology(
    int Slot,
    IReadOnlyList<PhysicalDeviceIdentity> Devices,
    HidCollection? Hid = null,
    bool HidRumble = false)
{
    internal bool SupportsRumble => Hid is null || HidRumble;
}

/// <summary>Physical XInput ownership, with the matching USB topology required before a slot is claimed.</summary>
internal sealed class StandardControllerSource(StandardControllerProfile profile) : IAsyncDisposable
{
    internal HapticCapabilities Haptics => profile.Haptics;

    private readonly Lock _gate = new();

    private readonly StandardControllerHidSource? _hid = profile.HidFallback is { } map
        ? new StandardControllerHidSource(map)
        : null;

    private Thread? _reader;
    private int _slot = -1;
    private IHidControllerRumble? _hidRumble;
    private CancellationTokenSource? _stop;

    public ValueTask DisposeAsync()
    {
        return StopAsync(CancellationToken.None);
    }

    internal StandardControllerTopology? Discover()
    {
        StandardControllerTopology? found = null;
        for (uint slot = 0; slot < 4; slot++)
        {
            if (StandardXInput.Capabilities(1, slot, 0, out var caps) != 0
                || !profile.UsbIds.Contains(new ControllerUsbId(caps.Vendor, caps.Product)))
            {
                continue;
            }

            var nodes = HidDevices.PresentNodes(caps.Vendor, [caps.Product]).Where(node =>
                node.ClassGuid == HidDevices.XnaCompositeClass || node.ClassGuid == HidDevices.XboxCompositeClass
                                                               || (node.ClassGuid == HidDevices.HidClass &&
                                                                   node.InstancePath.Contains("&IG_",
                                                                       StringComparison.OrdinalIgnoreCase))).ToArray();
            if (nodes.Length == 0 || nodes.Any(node => string.IsNullOrEmpty(node.PhysicalLocation)))
            {
                continue;
            }

            var location = nodes[0].PhysicalLocation!;
            if (profile.CompanionUsbIds.Count != 0)
            {
                var anchors =
                    profile.CompanionUsbIds.SelectMany(id => HidDevices.PresentNodes(id.Vendor, [id.Product]));
                if (!anchors.Any(anchor => SameBuiltInHub(location, anchor.PhysicalLocation)))
                {
                    continue;
                }
            }

            // Capabilities-ex does not identify the USB port. Refuse ambiguous identical pads rather
            // than taking an external controller or hiding a second pad with the same VID/PID.
            if (found is not null ||
                nodes.Any(node => !HidDevices.SamePhysicalLocation(location, node.PhysicalLocation!)))
            {
                return null;
            }

            found = new StandardControllerTopology((int)slot, HideSet(
                nodes.Select(PhysicalIdentities.FromNode), caps.Vendor, location));
        }

        if (found is not null || _hid?.Discover() is not { } hid)
        {
            return found;
        }

        if (profile.CompanionUsbIds.Count != 0)
        {
            var anchors = profile.CompanionUsbIds.SelectMany(id => HidDevices.PresentNodes(id.Vendor, [id.Product]));
            if (!anchors.Any(anchor => SameBuiltInHub(hid.PhysicalLocation, anchor.PhysicalLocation)))
            {
                return null;
            }
        }

        if (string.IsNullOrWhiteSpace(hid.InstancePath) || string.IsNullOrWhiteSpace(hid.PhysicalLocation))
        {
            return null;
        }

        return new StandardControllerTopology(-1,
            HideSet(new[] { PhysicalIdentities.FromCollection(hid) }
                .Concat(profile.HidCompanions?.Invoke(hid) ?? []), hid.VendorId, hid.PhysicalLocation), hid,
            profile.OpenHidRumble is not null);
    }

    /// <summary>The present identities plus the pad's nodes in every other mode Windows has seen it in.</summary>
    /// <remarks>
    ///     A pad that comes back under another of the profile's product IDs is then already hidden. The
    ///     profile's auxiliary leaves, which clients list as extra pads, are hidden with it.
    /// </remarks>
    private PhysicalDeviceIdentity[] HideSet(IEnumerable<PhysicalDeviceIdentity> present, ushort vendor,
        string location)
    {
        ushort[] products = [.. profile.UsbIds.Where(id => id.Vendor == vendor).Select(id => id.Product)];
        return
        [
            .. present.Concat(HidDevices.KnownControllerNodes(vendor, products, location,
                    profile.AuxiliaryHidCollections))
                .DistinctBy(device => device.InstancePath, StringComparer.OrdinalIgnoreCase)
        ];
    }

    internal static bool SameBuiltInHub(string controller, string companion)
    {
        static string? Parent(string location)
        {
            var last = location.LastIndexOf("#USB(", StringComparison.OrdinalIgnoreCase);
            if (last < 0)
            {
                return null;
            }

            var parent = location[..last];
            // Sharing only the machine's USB root says nothing about which peripheral is built in.
            return parent.Contains("#USB(", StringComparison.OrdinalIgnoreCase) ? parent : null;
        }

        var left = Parent(controller);
        var right = Parent(companion);
        return left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    internal void Start(StandardControllerTopology topology,
        Func<CanonicalButtons> buttons, Func<MotionSample?> motion,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        Func<StandardTouchSample>? touchpad = null)
    {
        if (topology.Hid is { } hid)
        {
            _hidRumble = profile.OpenHidRumble?.Invoke(hid);
            _hid!.Start(hid, buttons, motion, touchpad, publish, fault);
            return;
        }

        lock (_gate)
        {
            if (_reader is not null)
            {
                throw new InvalidOperationException("The physical controller reader is still owned.");
            }

            _slot = topology.Slot;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _reader = new Thread(() => Read(topology.Slot, buttons, motion, publish, fault, token, touchpad))
                { IsBackground = true, Name = "LibHandheld physical controller" };
            _reader.Start();
        }
    }

    internal void Rumble(float low, float high)
    {
        lock (_gate)
        {
            if (_hidRumble is { } output)
            {
                output.Write(low, high);
                return;
            }
            if (_slot < 0)
            {
                return;
            }

            var frame = new StandardXInput.Vibration
            {
                Low = (ushort)Math.Round(Math.Clamp(low, 0, 1) * ushort.MaxValue),
                High = (ushort)Math.Round(Math.Clamp(high, 0, 1) * ushort.MaxValue)
            };
            var status = StandardXInput.SetState((uint)_slot, ref frame);
            if (status != 0)
            {
                _slot = -1;
                throw new Win32Exception((int)status);
            }
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                _hidRumble?.Dispose();
            }
            catch (IOException)
            {
                // A disconnected HID output still closes its handle in Dispose's finally.
            }
            finally
            {
                _hidRumble = null;
            }
        }
        if (_hid is not null)
        {
            await _hid.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        Thread? reader;
        lock (_gate)
        {
            reader = _reader;
            try
            {
                Rumble(0, 0);
            }
            catch (Win32Exception)
            {
            }

            _stop?.Cancel();
        }

        if (reader is null)
        {
            return;
        }

        if (!await Task.Run(() => reader.Join(TimeSpan.FromSeconds(1)), CancellationToken.None)
                .WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("The physical controller reader has not released ownership.");
        }

        lock (_gate)
        {
            _reader = null;
            _slot = -1;
            _stop?.Dispose();
            _stop = null;
        }
    }

    private static void Read(int slot, Func<CanonicalButtons> buttons, Func<MotionSample?> motion,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish, Action<Exception> fault,
        CancellationToken stop,
        Func<StandardTouchSample>? touchpad)
    {
        try
        {
            using var ticker = new PrecisionTicker(TimeSpan.FromMilliseconds(8), stop);
            while (ticker.Wait())
            {
                var now = DateTimeOffset.UtcNow;
                var status = StandardXInput.StateEx((uint)slot, out var state);
                if (status != 0)
                {
                    publish(CanonicalControllerSample.Neutral(now), stop).GetAwaiter().GetResult();
                    fault(new IOException($"The physical XInput pad disconnected ({status})."));
                    return;
                }

                publish(
                    StandardControllerCodec.Decode(state.Gamepad, buttons(), motion(), now,
                        touchpad?.Invoke() ?? default), stop).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            fault(exception);
        }
    }
}

internal static class StandardControllerCodec
{
    internal static CanonicalControllerSample Decode(StandardXInput.Gamepad pad, CanonicalButtons oem,
        MotionSample? motion, DateTimeOffset now, StandardTouchSample touchpad = default)
    {
        var buttons = oem | (touchpad.Touch ? CanonicalButtons.LeftPadTouch : 0)
                          | (touchpad.Click ? CanonicalButtons.LeftPadClick : 0);
        ReadOnlySpan<CanonicalButtons> map =
        [
            CanonicalButtons.DPadUp, CanonicalButtons.DPadDown,
            CanonicalButtons.DPadLeft, CanonicalButtons.DPadRight, CanonicalButtons.Menu, CanonicalButtons.View,
            CanonicalButtons.LeftStick, CanonicalButtons.RightStick, CanonicalButtons.LeftShoulder,
            CanonicalButtons.RightShoulder, CanonicalButtons.Guide, CanonicalButtons.None,
            CanonicalButtons.A, CanonicalButtons.B, CanonicalButtons.X, CanonicalButtons.Y
        ];
        for (var bit = 0; bit < map.Length; bit++)
        {
            if ((pad.Buttons & (1 << bit)) != 0)
            {
                buttons |= map[bit];
            }
        }

        return new CanonicalControllerSample
        {
            Timestamp = now, Buttons = buttons, LeftStickX = Axis(pad.LeftX), LeftStickY = Axis(pad.LeftY),
            RightStickX = Axis(pad.RightX), RightStickY = Axis(pad.RightY),
            LeftTrigger = pad.LeftTrigger / 255f, RightTrigger = pad.RightTrigger / 255f, Motion = motion,
            LeftPadX = touchpad.X, LeftPadY = touchpad.Y, LeftPadForce = touchpad.Touch ? 1 : 0
        };
    }

    private static float Axis(short value)
    {
        return Math.Clamp(value / 32767f, -1, 1);
    }
}

internal static class StandardXInput
{
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    internal static extern uint StateEx(uint slot, out State state);

    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    internal static extern uint Capabilities(uint reserved, uint slot, uint flags, out Caps caps);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    internal static extern uint SetState(uint slot, ref Vibration vibration);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Gamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct State
    {
        public uint Packet;
        public Gamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Vibration
    {
        public ushort Low;
        public ushort High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Caps
    {
        public byte Type;
        public byte Subtype;
        public ushort Flags;
        public Gamepad Gamepad;
        public Vibration Vibration;
        public ushort Vendor;
        public ushort Product;
        public ushort Revision;
        public uint Xid;
    }
}
