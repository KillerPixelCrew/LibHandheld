using System.Text.Json.Serialization;
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

    private readonly SteamMachineRecovery _journal = new();
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
            Display = Label("Lighting"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile
        },
        new CapabilityDescriptor
        {
            CapabilityId = BrightnessId, Role = CapabilityRole.LightingBrightness,
            ValueKind = CapabilityValueKind.Integer,
            Display = Label("Brightness"), Minimum = 0, Maximum = 100, Step = 1, Unit = CapabilityUnit.Percent,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        },
        Color("left", "Left colour"), Color("right", "Right colour"),
        new CapabilityDescriptor
        {
            CapabilityId = EffectId, Role = CapabilityRole.LightingEffect, ValueKind = CapabilityValueKind.Choice,
            Display = Label("Lighting effect"), SupportsRead = true, SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices = Array.AsReadOnly(Effects.Select(effect => new CapabilityChoice(effect, Label(effect))).ToArray())
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
        ObservedAt = _observedAt.TryGetValue((descriptor.CapabilityId, descriptor.InstanceId), out var at) ? at : null,
        DescriptorGeneration = 0, CycleGeneration = 0
    }).ToArray();

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _journal.OpenAsync(context.StateDirectory, token).ConfigureAwait(false);
        _module = (open ?? PawnIoModule.OpenValveLeds)();
        try
        {
            if (_module.Execute("ioctl_get_led_num", [], 1)[0] != 17)
            {
                throw new IOException("The Valve LED strip did not report its exact 17-LED shape.");
            }

            if (_journal.EntryFor(ServiceId) is { } previous)
            {
                if (previous.FirmwareIdentity != context.Firmware)
                {
                    throw new InvalidOperationException("Pending LED state belongs to another firmware identity.");
                }

                if (previous.Status == DeviceRecoveryStatus.Pending)
                {
                    await RestoreAsync(token).ConfigureAwait(false);
                }
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
            if (_journal.EntryFor(ServiceId) is null)
            {
                await _journal.BeginAsync(ServiceId, context.Firmware, Capture(), token).ConfigureAwait(false);
            }
            else if (_journal.EntryFor(ServiceId) is { Status: not DeviceRecoveryStatus.Pending } original)
            {
                await _journal.BeginAsync(ServiceId, context.Firmware, original.OriginalState, token)
                    .ConfigureAwait(false);
            }

            // Init changes a brightness-control bit with no getter/restorer in the signed module.
            // Persist that limitation before the explicit brightness request, rather than writing it during acquisition.
            if (command.CapabilityId == BrightnessId)
            {
                await _journal.MarkInitializationAsync(context.Firmware, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            dispatched = true;
            HardwareBus.Run(@"Global\Access_ISABUS.HTP.Method", TimeSpan.FromSeconds(2), () =>
            {
                switch (command.CapabilityId)
                {
                    case PowerId:
                        _module.Execute("ioctl_set_enabled", [requested.BooleanValue == true ? 1 : 0], 0); break;
                    case BrightnessId:
                        _module.Execute("ioctl_init", [], 0);
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

            return CommandResults.Unverified(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message,
                    RollbackResult.NotRequired)
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
        if (_module is null || _journal.PendingOriginalFor(ServiceId) is not { } original)
        {
            return;
        }

        var complete = !original.InitializationChanged && original.Enabled is not null &&
                       original.Brightness is not null
                       && original.Effect is not null && original.Colors.All(color => color is not null);
        try
        {
            HardwareBus.Run(@"Global\Access_ISABUS.HTP.Method", TimeSpan.FromSeconds(2), () =>
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
            if (_journal.EntryFor("fremont-init") is not null)
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

        return Set(_journal.OutstandingEntries.Count > 0
            ? DeviceServiceState.ReleasedUnverified
            : DeviceServiceState.Idle);
    }

    private static CapabilityDisplay Label(string value)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = value };
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
            Display = Label(label), SupportsRead = true, SupportsWrite = true,
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

internal sealed class SteamMachineRecovery()
    : DeviceRecoveryJournal<SteamMachineOriginal>(SteamMachineRecoveryJson.Default.Document)
{
    internal ValueTask OpenAsync(string directory, CancellationToken token)
    {
        return LoadAsync(Path.Combine(directory, "steam-machine-native"), token);
    }

    internal async ValueTask MarkInitializationAsync(string firmware, CancellationToken token)
    {
        // This module offers no getter for the brightness-control bit changed by ioctl_init.
        // Keep the unknown original separate from the LED colours and readable values.
        await BeginAsync("fremont-init", firmware, new SteamMachineOriginal
            { Colors = new int?[17], InitializationChanged = true }, token).ConfigureAwait(false);
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<SteamMachineOriginal> entry)
    {
        var state = entry.OriginalState;
        if (entry.ServiceId is not ("fremont-lighting" or "fremont-init") || state.Enabled is not (null or 0 or 1)
                                                                          || state.Brightness is < 0 or > 255 ||
                                                                          state.Effect is < 0 or > 7 ||
                                                                          state.Colors.Length != 17
                                                                          || state.Colors.Any(color =>
                                                                              color is < 0 or > 0xFFFFFF))
        {
            throw new InvalidDataException("The Fremont LED recovery entry is invalid.");
        }
    }
}

[JsonSerializable(typeof(DeviceRecoveryDocument<SteamMachineOriginal>), TypeInfoPropertyName = "Document")]
internal partial class SteamMachineRecoveryJson : JsonSerializerContext;
