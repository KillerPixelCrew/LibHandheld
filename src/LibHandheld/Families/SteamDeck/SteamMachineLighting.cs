using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.SteamDeck;

/// <summary>Fremont's 17-LED strip, independent of the Deck's Neptune controller and EC memory interface.</summary>
internal sealed class SteamMachineLighting(ProfiledFeatureContext context, Func<IPawnIoModule>? open = null)
    : ProfiledFeatureService("fremont-lighting")
{
    private const string PowerId = "lighting.power";
    private const string BrightnessId = "lighting.brightness";
    private const string ColorId = "lighting.zone-color";
    private const string EffectId = "lighting.effect";

    private static readonly string[] Effects =
        ["patrol", "breathing", "factory", "solid", "off", "rainbow", "demo", "ambilight"];

    private readonly DeviceRecoveryJournal _journal = context.Journal;
    private readonly Dictionary<(string Id, string? Instance), CapabilityValue> _observed = [];
    private readonly Dictionary<(string Id, string? Instance), DateTimeOffset> _observedAt = [];
    private int _effect = 3;
    private int _left;
    private IPawnIoModule? _module;
    private int _right;
    public override bool Suspendable => true;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors { get; } = Array.AsReadOnly<CapabilityDescriptor>(
    [
        new CapabilityDescriptor
        {
            CapabilityId = PowerId, Role = CapabilityRole.LightingPower, ValueKind = CapabilityValueKind.Boolean,
            Display = CapabilityDescriptors.Label("Lighting"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        },
        new CapabilityDescriptor
        {
            CapabilityId = BrightnessId, Role = CapabilityRole.LightingBrightness,
            ValueKind = CapabilityValueKind.Integer,
            Display = CapabilityDescriptors.Label("Brightness"), Minimum = 0, Maximum = 100, Step = 1,
            Unit = CapabilityUnit.Percent,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        },
        Color("left", "Left colour"), Color("right", "Right colour"),
        new CapabilityDescriptor
        {
            CapabilityId = EffectId, Role = CapabilityRole.LightingEffect, ValueKind = CapabilityValueKind.Choice,
            Display = CapabilityDescriptors.Label("Lighting effect"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices = Array.AsReadOnly(Effects
                .Select(effect => new CapabilityChoice(effect, CapabilityDescriptors.Label(effect))).ToArray())
        }
    ]);

    internal override IReadOnlyList<CapabilityState> States => Descriptors.Select(descriptor => new CapabilityState
    {
        CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
        Available = State == DeviceServiceState.Owned && (descriptor.InstanceId != "right" || _effect == 7),
        Reason = descriptor.InstanceId == "right" && _effect != 7
            ? Missing("The right endpoint colour requires Ambilight.")
            : Reason,
        ObservedValue = _observed.GetValueOrDefault((descriptor.CapabilityId, descriptor.InstanceId)),
        Quality = _observed.ContainsKey((descriptor.CapabilityId, descriptor.InstanceId))
            ? HardwareStateQuality.Observed
            : HardwareStateQuality.Unknown,
        ObservedAt = _observedAt.TryGetValue((descriptor.CapabilityId, descriptor.InstanceId), out var at) ? at : null
    }).ToArray();

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _module = (open ?? PawnIoModule.OpenValveLeds)();
        try
        {
            await _journal.ReconcileAsync<SteamMachineOriginal>(ServiceId, context.Firmware,
                (_, ct) => RestoreAsync(ct), token).ConfigureAwait(false);
            await _journal.BeginAsync(ServiceId, context.Firmware, Capture, token).ConfigureAwait(false);
            await _journal.BeginAsync("fremont-init", context.Firmware,
                    () => new SteamMachineOriginal { Colors = new int?[17], InitializationChanged = true }, token)
                .ConfigureAwait(false);
            HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2),
                () => _module.Execute("ioctl_init", [], 0));
            if (_module.Execute("ioctl_get_led_num", [], 1)[0] != 17)
            {
                throw new IOException("The Valve LED strip did not report its exact 17-LED shape.");
            }

            await RefreshAsync(token).ConfigureAwait(false);
            return Set(DeviceServiceState.Owned);
        }
        catch
        {
            _module.Dispose();
            _module = null;
            throw;
        }
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_module is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The signed Fremont LED module is unavailable.");
        }

        var descriptor = Descriptors.SingleOrDefault(item =>
            item.CapabilityId == command.CapabilityId && item.InstanceId == command.InstanceId);
        if (descriptor is null || command.RequestedValue is not { } requested ||
            !CapabilityValueValidation.ValueMatches(requested, descriptor, out _))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "This value is outside the Fremont LED capability.");
        }

        if (command.CapabilityId == ColorId && command.InstanceId == "right" && _effect != 7)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "A second endpoint colour requires the Ambilight effect.");
        }

        var dispatched = false;
        try
        {
            await _journal.BeginAsync(ServiceId, context.Firmware, Capture, token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            dispatched = true;
            HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
            {
                switch (command.CapabilityId)
                {
                    case PowerId:
                        _module.Execute("ioctl_set_enabled", [requested.BooleanValue == true ? 1 : 0], 0); break;
                    case BrightnessId:
                        _module.Execute("ioctl_set_brightness_scale", [requested.IntegerValue!.Value * 255 / 100], 0);
                        break;
                    case EffectId:
                        _effect = Array.IndexOf(Effects, requested.ChoiceValue!);
                        if (_effect is 3 or 7)
                        {
                            WriteColors(_left, _effect == 7 ? _right : _left);
                        }

                        _module.Execute("ioctl_set_effect", [_effect], 0);
                        break;
                    case ColorId:
                        if (command.InstanceId == "left")
                        {
                            _left = requested.ColorValue!.Value;
                        }
                        else
                        {
                            _right = requested.ColorValue!.Value;
                        }

                        WriteColors(_left, _effect == 7 ? _right : _left);
                        _module.Execute("ioctl_set_effect", [_effect], 0);
                        break;
                }

                return true;
            });
            Observe(command.CapabilityId, command.InstanceId, requested);
            if (command.CapabilityId is ColorId or EffectId)
            {
                Observe(ColorId, "left", CapabilityValue.Color(_left));
                Observe(ColorId, "right", CapabilityValue.Color(_effect == 7 ? _right : _left));
            }

            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_module is null)
        {
            return ValueTask.CompletedTask;
        }

        var enabled = ReadScalar("ioctl_get_enabled");
        var brightness = ReadScalar("ioctl_get_brightness_scale");
        var effect = ReadScalar("ioctl_get_effect");
        if (enabled is 0 or 1)
        {
            Observe(PowerId, null, CapabilityValue.Boolean(enabled == 1));
        }

        if (brightness is >= 0 and <= 255)
        {
            Observe(BrightnessId, null, CapabilityValue.Integer((int)Math.Round(brightness.Value * 100d / 255)));
        }

        if (effect is >= 0 and <= 7)
        {
            _effect = (int)effect.Value;
            Observe(EffectId, null, CapabilityValue.Choice(Effects[_effect]));
        }

        if (ReadColor(0) is { } left)
        {
            _left = left;
            Observe(ColorId, "left", CapabilityValue.Color(left));
        }

        if (ReadColor(16) is { } right)
        {
            _right = right;
            Observe(ColorId, "right", CapabilityValue.Color(right));
        }

        return ValueTask.CompletedTask;
    }

    private SteamMachineOriginal Capture()
    {
        return new SteamMachineOriginal
        {
            Enabled = ReadScalar("ioctl_get_enabled"), Brightness = ReadScalar("ioctl_get_brightness_scale"),
            Effect = ReadScalar("ioctl_get_effect"),
            Colors = Enumerable.Range(0, 17).Select(ReadColor).ToArray()
        };
    }

    private long? ReadScalar(string function)
    {
        try
        {
            return _module!.Execute(function, [], 1)[0];
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private int? ReadColor(int led)
    {
        try
        {
            var color = _module!.Execute("ioctl_get_led_color", [led], 3);
            return color.All(value => value is >= 0 and <= 255)
                ? ((int)color[0] << 16) | ((int)color[1] << 8) | (int)color[2]
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    internal static int Blend(int left, int right, int led)
    {
        if (led is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(led));
        }

        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);

        int Channel(int shift)
        {
            var start = (left >> shift) & 255;
            var end = (right >> shift) & 255;
            return start + (end - start) * led / 16;
        }
    }

    private void WriteColors(int left, int right)
    {
        for (var led = 0; led < 17; led++)
        {
            WriteColor(led, Blend(left, right, led));
        }
    }

    private void WriteColor(int led, int color)
    {
        _module!.Execute("ioctl_set_led_color", [led, (color >> 16) & 255, (color >> 8) & 255, color & 255], 0);
    }

    private async ValueTask RestoreAsync(CancellationToken token)
    {
        if (_module is null || _journal.PendingOriginalFor<SteamMachineOriginal>(ServiceId) is not { } original)
        {
            return;
        }

        var complete = !original.InitializationChanged && original.Enabled is not null &&
                       original.Brightness is not null
                       && original.Effect is not null && original.Colors.All(color => color is not null);
        try
        {
            HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
            {
                for (var led = 0; led < 17; led++)
                {
                    if (original.Colors[led] is { } color)
                    {
                        WriteColor(led, color);
                    }
                }

                if (original.Brightness is { } brightness)
                {
                    _module.Execute("ioctl_set_brightness_scale", [brightness], 0);
                }

                if (original.Effect is { } effect)
                {
                    _module.Execute("ioctl_set_effect", [effect], 0);
                }

                if (original.Enabled is { } enabled)
                {
                    _module.Execute("ioctl_set_enabled", [enabled], 0);
                }

                return true;
            });
            var captured = Capture();
            complete &= captured.Enabled == original.Enabled && captured.Brightness == original.Brightness
                                                             && captured.Effect == original.Effect &&
                                                             captured.Colors.SequenceEqual(original.Colors);
            await _journal.SetStatusAsync(ServiceId,
                    complete ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoredUnverified, token)
                .ConfigureAwait(false);
            if (_journal.EntryFor<SteamMachineOriginal>("fremont-init") is not null)
            {
                await _journal.SetStatusAsync("fremont-init", DeviceRecoveryStatus.RestoredUnverified, token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            await _journal.SetStatusAsync(ServiceId, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    internal override ValueTask DisposeAsync()
    {
        _module?.Dispose();
        _module = null;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            await RestoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _module?.Dispose();
            _module = null;
            _observed.Clear();
            _observedAt.Clear();
        }

        return Set(_journal.Entries<SteamMachineOriginal>().Count > 0
            ? DeviceServiceState.ReleasedUnverified
            : DeviceServiceState.Idle);
    }


    private void Observe(string id, string? instance, CapabilityValue value)
    {
        _observed[(id, instance)] = value;
        _observedAt[(id, instance)] = DateTimeOffset.UtcNow;
    }

    private static CapabilityDescriptor Color(string instance, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = ColorId, InstanceId = instance, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = CapabilityDescriptors.Label(label), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        };
    }
}

internal sealed record SteamMachineOriginal
{
    public long? Enabled { get; init; }
    public long? Brightness { get; init; }
    public long? Effect { get; init; }
    public int?[] Colors { get; init; } = [];
    public bool InitializationChanged { get; init; }
}
