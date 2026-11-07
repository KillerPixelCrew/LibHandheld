using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;
using Microsoft.Win32.SafeHandles;

namespace LibHandheld.Families.Acer;

/// <summary>The Atlas's separate ENE rear-logo LED; controller ring profiles never address it.</summary>
/// <remarks>
///     A1 lists targets, A2 selects one for the A3 capability query, and A4 sets the selected target's
///     effect, brightness, speed, RGB and LED mask. There is no current-lighting getter. Values start
///     unknown and are published only after an explicit complete write. Nothing is sent on stop.
/// </remarks>
internal sealed class AcerLogoLightingService() : ProfiledFeatureService("acer-logo-lighting")
{
    private const string Instance = "logo";
    private readonly ObservedValues _observed = new();
    private IReadOnlyList<CapabilityDescriptor> _controls = [];
    private SafeFileHandle? _handle;
    private string _effect = "solid";
    private int _brightness = 100;
    private int _speed = 5;
    private int _color = 0xFFFFFF;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _controls;
    internal override IReadOnlyList<CapabilityState> States => _controls.Select(control =>
        ProviderControls.Observed(control, _observed, State == DeviceServiceState.Owned, Reason)).ToArray();
    public override bool Suspendable => true;

    public override ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        // This is I2C HID (ENEK5130), so USB VID_/PID_ path filtering cannot discover it.
        var collection = HidDevices.Unique(HidDevices.EnumerateAll(),
            item => item.VendorId == 0x0CF2 && item.ProductId == 0x5130
                    && item.UsagePage == 0xFF5A && item.Usage == 1 && item.ReleaseNumber == 1
                    && item.FeatureLength == 11);
        if (collection is null)
        {
            _controls = [];
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The Atlas rear-logo lighting interface is unavailable.")));
        }

        try
        {
            _handle = HidDevices.Open(collection, overlapped: false);
            var report = new byte[11];
            report[0] = 0xA1;
            ReadFeature(report);
            if (report[0] != 0xA1 || report[1] is < 1 or > 9 || !report.AsSpan(2, report[1]).Contains((byte)0x80))
            {
                throw new IOException("The ENE lighting interface does not list the rear logo.");
            }
            Array.Clear(report);
            report[0] = 0xA2;
            report[1] = 0x80;
            if (!NativeHid.HidD_SetFeature(_handle, report, report.Length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The logo capability selection failed.");
            }
            Array.Clear(report);
            report[0] = 0xA3;
            ReadFeature(report);
            if (report[0] != 0xA3 || report[1] != 0x80 || report[2] != 1 || report[4] != 1)
            {
                throw new IOException("The rear-logo capability response is unsupported.");
            }
            var modes = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(5));
            var effects = new[] { ("off", 1), ("solid", 2), ("breathing", 4) }
                .Where(effect => (modes & (1U << (effect.Item2 - 1))) != 0).Select(effect => effect.Item1).ToArray();
            if (!effects.Contains("solid") || !effects.Contains("off"))
            {
                throw new IOException("The rear logo does not declare steady color and off support.");
            }
            _controls =
            [
                CapabilityDescriptors.Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                    CapabilityDescriptors.Label("Rear logo brightness"), minimum: 0, maximum: 100,
                    CapabilityUnit.Percent, read: false, write: true, instance: Instance),
                CapabilityDescriptors.Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect,
                    CapabilityDescriptors.Label("Rear logo effect"), CapabilityDescriptors.Choices(effects),
                    read: false, write: true, instance: Instance),
                CapabilityDescriptors.Integer(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed,
                    CapabilityDescriptors.Label("Rear logo speed"), minimum: 0, maximum: 10,
                    CapabilityUnit.None, read: false, write: true, instance: Instance),
                CapabilityDescriptors.Color(CapabilityIds.LightingColor, Instance,
                    CapabilityDescriptors.Label("Rear logo color"), read: false, CapabilityPersistence.Volatile)
            ];
            return ValueTask.FromResult(Set(DeviceServiceState.Owned));
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            _handle?.Dispose();
            _handle = null;
            _controls = [];
            return ValueTask.FromResult(Set(DeviceServiceState.Passive, Missing(exception.Message)));
        }
    }

    private void ReadFeature(byte[] report)
    {
        if (!NativeHid.HidD_GetFeature(_handle!, report, report.Length))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The logo capability query failed.");
        }
    }

    /// <summary>Encodes the 11-byte A4 report. The final word is a one-LED mask, not a duration.</summary>
    internal static byte[] Packet(string effect, int brightness, int speed, int color)
    {
        if (brightness is < 0 or > 100 || speed is < 0 or > 10 || color is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }
        var mode = effect switch
        {
            "off" => (byte)1, "solid" => (byte)2, "breathing" => (byte)4,
            _ => throw new ArgumentException("Unsupported rear-logo effect.", nameof(effect))
        };
        return [0xA4, 0x80, mode, (byte)brightness, (byte)speed, 0,
            (byte)(color >> 16), (byte)(color >> 8), (byte)color, 1, 0];
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_handle is null || State != DeviceServiceState.Owned)
        {
            return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The rear-logo lighting interface is unavailable."));
        }
        var dispatched = false;
        try
        {
            var effect = _effect;
            var brightness = _brightness;
            var speed = _speed;
            var color = _color;
            switch (command.CapabilityId)
            {
                case CapabilityIds.LightingBrightness:
                    brightness = command.RequestedValue!.IntegerValue!.Value;
                    break;
                case CapabilityIds.LightingEffect:
                    effect = command.RequestedValue!.ChoiceValue!;
                    break;
                case CapabilityIds.LightingSpeed:
                    speed = command.RequestedValue!.IntegerValue!.Value;
                    break;
                case CapabilityIds.LightingColor:
                    color = command.RequestedValue!.ColorValue!.Value;
                    break;
                default:
                    throw new ArgumentException("Unknown rear-logo control.");
            }
            var report = Packet(effect, brightness, speed, color);
            token.ThrowIfCancellationRequested();
            dispatched = true;
            if (!NativeHid.HidD_SetFeature(_handle, report, report.Length))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Rear-logo write failed; effect unknown.");
            }
            _effect = effect;
            _brightness = brightness;
            _speed = speed;
            _color = color;
            _observed.Written(CapabilityIds.LightingEffect, CapabilityValue.Choice(effect), Instance);
            _observed.Written(CapabilityIds.LightingBrightness, CapabilityValue.Integer(brightness), Instance);
            _observed.Written(CapabilityIds.LightingSpeed, CapabilityValue.Integer(speed), Instance);
            _observed.Written(CapabilityIds.LightingColor, CapabilityValue.Color(color), Instance);
            return ValueTask.FromResult(CommandResults.Applied(command));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Clear();
            }
            return ValueTask.FromResult(CommandResults.TransportFailure(command, dispatched, exception));
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        await DisposeAsync().ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    internal override ValueTask DisposeAsync()
    {
        _handle?.Dispose();
        _handle = null;
        return ValueTask.CompletedTask;
    }
}
