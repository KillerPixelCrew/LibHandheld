using System.Globalization;
using System.Management;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.OneXPlayer;

/// <summary>EC fan/takeover and vendor preferences; CPU and ordinary controller lifetime stay in the shared engine.</summary>
internal sealed class OneXNativeService(
    ProfiledFeatureContext context,
    OneXProtocolProfile profile,
    Func<OneXEcKind, IEmbeddedController>? openEc = null,
    Func<IEmbeddedController>? openAcpi = null) : ProfiledFeatureService("onexplayer-controls")
{
    private readonly Dictionary<string, int> _colors = new(StringComparer.Ordinal)
        { ["primary"] = 0xFFFFFF, ["secondary"] = 0xFFFFFF };

    private readonly Dictionary<(string, string?), CapabilityValue> _observed = [];
    private readonly Dictionary<(string, string?), DateTimeOffset> _observedAt = [];
    private int _brightness = 100;
    private bool _chargeSupported;
    private IEmbeddedController? _ec;

    private string _effect = "solid";
    private DeviceRecoveryJournal? _journal;

    private OneXVendorTransport? _lights;
    private bool _manage = context.ControllerManagementEnabled;
    private bool _takeoverActive;
    private IEmbeddedController? _takeoverEc;
    private bool _unknownOriginal;
    private OneXVendorTransport? _vibration;

    public override bool Suspendable => true;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => Describe();

    internal override IReadOnlyList<CapabilityState> States => Array.AsReadOnly(Descriptors.Select(item =>
    {
        var lights = item.Role is CapabilityRole.LightingBrightness or CapabilityRole.LightingEffect
            or CapabilityRole.LightingZoneColor;
        var available = item.CapabilityId == "haptic.strength" ? _vibration is not null
            : lights ? _lights is not null : _ec is not null;
        var value = _observed.GetValueOrDefault((item.CapabilityId, item.InstanceId));
        return new CapabilityState
        {
            CapabilityId = item.CapabilityId, InstanceId = item.InstanceId,
            Available = available, ObservedValue = value,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
            ObservedAt = value is null ? null : _observedAt.GetValueOrDefault((item.CapabilityId, item.InstanceId)),

            Reason = available
                ? null
                : Missing("The model's native transport is unavailable.")
        };
    }).ToArray());

    internal override ValueTask DisposeAsync()
    {
        if (!ReferenceEquals(_takeoverEc, _ec))
        {
            _takeoverEc?.Dispose();
        }

        _takeoverEc = null;
        _ec?.Dispose();
        _ec = null;
        _lights?.Dispose();
        _lights = null;
        _vibration?.Dispose();
        _vibration = null;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _journal = context.Journal;
        try
        {
            _ec = openEc is not null
                ? openEc(profile.Ec)
                : profile.Ec switch
                {
                    OneXEcKind.Acpi => PawnIoEmbeddedController.OpenAcpi(),
                    OneXEcKind.Indexed => PawnIoEmbeddedController.OpenIndexed(), _ => new OneXWmiEc()
                };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ec = null;
        }

        try
        {
            _lights = OneXVendorTransport.Open(profile.Lighting);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _lights = null;
        }

        try
        {
            _vibration = profile.Vibration ? OneXVendorTransport.Open(OneXLightingKind.HidV1) : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _vibration = null;
        }

        // Uncertainty survives suspend/resume. Reopening a handle does not prove that the previous
        // write failed, and must not cause a saved preference to be sent again automatically.
        _chargeSupported = profile.Charge != OneXChargeKind.None && ReadChargeFirmwareSupport();
        if (_ec is not null)
        {
            foreach (var entry in _journal.Entries<OneXRegisterState>())
            {
                await _journal.ReconcileAsync<OneXRegisterState>(entry.ServiceId, context.Firmware,
                    (original, ct) => RestoreAsync(entry.ServiceId, original, ct), token).ConfigureAwait(false);
            }

            if (_manage)
            {
                await TakeoverAsync(token).ConfigureAwait(false);
            }

            await RefreshAsync(token).ConfigureAwait(false);
        }

        return _ec is null && _lights is null && _vibration is null
            ? Set(DeviceServiceState.Passive, Missing("No ONEXPLAYER native control transport is present."))
            : Set(DeviceServiceState.Owned);
    }

    internal override async ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        _manage = enabled;
        if (_ec is null)
        {
            return;
        }

        if (enabled)
        {
            await TakeoverAsync(token).ConfigureAwait(false);
        }
        else if (!enabled && _journal?.PendingOriginalFor<OneXRegisterState>("takeover") is { } original)
        {
            await RestoreAsync("takeover", original, token).ConfigureAwait(false);
        }
        else if (!enabled && _takeoverActive && profile.Ec == OneXEcKind.Wmi)
        {
            token.ThrowIfCancellationRequested();
            WriteTakeoverBit(_takeoverEc!, false);
            _takeoverActive = false;
        }
    }

    private async ValueTask TakeoverAsync(CancellationToken token)
    {
        var wmi = profile.Ec == OneXEcKind.Wmi;
        _takeoverEc ??= wmi ? _ec : OpenAcpi();
        var addresses = wmi ? new ushort[] { 0x04EB } :
            profile.ExtraTakeover is { } extra ? [0xF1, 0xF2, extra] : [0xF1, 0xF2];
        await CaptureAsync("takeover", addresses, !wmi, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (wmi)
        {
            WriteTakeoverBit(_takeoverEc!, true);
            _takeoverActive = true;
        }
        else
        {
            _takeoverEc!.WriteByte(0xF1, 0x40);
            await Task.Delay(50, token).ConfigureAwait(false);
            _takeoverEc.WriteByte(0xF2, 2);
            await Task.Delay(50, token).ConfigureAwait(false);
            if (profile.ExtraTakeover is { } takeoverAddress)
            {
                _takeoverEc.WriteByte(takeoverAddress, profile.ExtraValue);
            }
        }
    }

    private async ValueTask CaptureAsync(string id, ushort[] addresses, bool acpi, CancellationToken token)
    {
        var transport = acpi ? _takeoverEc : _ec;
        var captured = await _journal!.BeginAsync(id, context.Firmware, () =>
        {
            var values = new byte[addresses.Length];
            for (var index = 0; index < addresses.Length; index++)
            {
                values[index] = transport!.ReadByte(addresses[index]);
            }

            return new OneXRegisterState { Addresses = addresses, Values = values, Acpi = acpi };
        }, token).ConfigureAwait(false);
        _unknownOriginal |= captured is null;
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        var value = command.RequestedValue!;
        var sent = false;
        var lighting = command.CapabilityId is CapabilityIds.LightingBrightness or CapabilityIds.LightingEffect
            or CapabilityIds.LightingColor;
        if (command.CapabilityId == "haptic.strength" ? _vibration is null
            : lighting ? _lights is null : _ec is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The native transport is unavailable.");
        }

        try
        {
            switch (command.CapabilityId)
            {
                case CapabilityIds.FanMode:
                case "fan.duty":
                    await CaptureAsync("fan", [profile.FanDuty, profile.FanMode], false, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var mode = command.CapabilityId == "fan.duty" ? "custom" : value.ChoiceValue!;
                    var percent = command.CapabilityId == "fan.duty" ? value.IntegerValue!.Value : 100;
                    sent = true;
                    if (command.CapabilityId == "fan.duty" || mode == "full-speed")
                    {
                        _ec!.WriteByte(profile.FanDuty, EncodeDuty(profile, percent));
                    }

                    _ec!.WriteByte(profile.FanMode, (byte)(mode == "automatic" ? 0 : 1));
                    Observe(CapabilityIds.FanMode, null, CapabilityValue.Choice(mode));
                    if (command.CapabilityId == "fan.duty" || mode == "full-speed")
                    {
                        Observe("fan.duty", null, CapabilityValue.Integer(percent));
                    }

                    break;
                case CapabilityIds.ChargeLimit:
                    token.ThrowIfCancellationRequested();
                    sent = true;
                    _ec!.WriteByte(0x4A3, (byte)value.IntegerValue!.Value);
                    break;
                case "battery.bypass-charging":
                    token.ThrowIfCancellationRequested();
                    sent = true;
                    _ec!.WriteByte(0x4A4,
                        value.ChoiceValue switch
                        {
                            "disabled" => 0, "until-sleep" => 1, "always" => 3,
                            _ => throw new ArgumentOutOfRangeException(nameof(command))
                        });
                    break;
                case CapabilityIds.LightingBrightness:
                    _brightness = value.IntegerValue!.Value;
                    foreach (var side in PrimarySides().Concat(SecondarySides()))
                    {
                        sent = true;
                        _lights!.Write(OneXCommandCodec.Brightness(profile.Lighting, _brightness, side), token);
                        await Gap(token).ConfigureAwait(false);
                    }

                    break;
                case CapabilityIds.LightingEffect:
                    _effect = value.ChoiceValue!;
                    if (_effect == "off")
                    {
                        foreach (var side in PrimarySides().Concat(SecondarySides()))
                        {
                            sent = true;
                            _lights!.Write(OneXCommandCodec.Brightness(profile.Lighting, 0, side), token);
                            await Gap(token).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        foreach (var side in PrimarySides().Concat(SecondarySides()))
                        {
                            sent = true;
                            _lights!.Write(OneXCommandCodec.Brightness(profile.Lighting, _brightness, side), token);
                            await Gap(token).ConfigureAwait(false);
                        }

                        await ApplyColorAsync("primary", token, () => sent = true).ConfigureAwait(false);
                        if (SecondarySides().Count != 0 && profile.Lighting != OneXLightingKind.Serial)
                        {
                            await ApplyColorAsync("secondary", token, () => sent = true).ConfigureAwait(false);
                        }
                    }

                    break;
                case CapabilityIds.LightingColor:
                    _colors[command.InstanceId ?? "primary"] = value.ColorValue!.Value;
                    await ApplyColorAsync(command.InstanceId ?? "primary", token, () => sent = true)
                        .ConfigureAwait(false);
                    break;
                case "haptic.strength":
                    sent = true;
                    _vibration!.Write(OneXCommandCodec.Vibration(value.IntegerValue!.Value, profile.SimpleVibration),
                        token);
                    break;
                default:
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "This model has no matching native operation.");
            }

            Observe(command.CapabilityId, command.InstanceId, value);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return sent
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    internal static byte EncodeDuty(OneXProtocolProfile profile, int percentage)
    {
        var scaled = Math.Clamp(percentage, 0, 100) * (profile.MaximumDuty - profile.MinimumDuty) / 100d +
                     profile.MinimumDuty;
        // Legacy ACPI overrides cast the PWM value; indexed and X2 paths use a rounded byte.
        return checked((byte)(profile.Ec == OneXEcKind.Acpi ? scaled : Math.Round(scaled)));
    }

    private async ValueTask ApplyColorAsync(string zone, CancellationToken token, Action dispatch)
    {
        var sides = zone == "secondary" ? SecondarySides() : PrimarySides();
        foreach (var side in sides)
        {
            dispatch();
            _lights!.Write(OneXCommandCodec.Lighting(profile.Lighting,
                ColorEffect(profile.Lighting, zone, _effect), _colors[zone], side), token);
            await Gap(token).ConfigureAwait(false);
        }
    }

    private Task Gap(CancellationToken token)
    {
        return Task.Delay(LightingDelay(profile.Lighting), token);
    }

    internal static string ColorEffect(OneXLightingKind kind, string zone, string effect)
    {
        // HC's serial presets address controller position zero only. Rear positions retain their
        // independently selected solid color, including when authored under a controller preset.
        return effect == "off" || kind == OneXLightingKind.Serial && zone == "secondary" ? "solid" : effect;
    }

    internal static int LightingDelay(OneXLightingKind kind)
    {
        return kind is OneXLightingKind.Serial or OneXLightingKind.HidV1X2 ? 200 : 50;
    }

    internal static void WriteTakeoverBit(IEmbeddedController transport, bool enabled)
    {
        // HC X2/3 owns only bit 6. A failed live read cannot safely synthesize the other bits.
        transport.Transaction(() =>
        {
            var current = transport.ReadByte(0x04EB);
            transport.WriteByte(0x04EB, (byte)(enabled ? current | 0x40 : current & ~0x40));
            return true;
        });
    }

    private IReadOnlyList<byte> PrimarySides()
    {
        return profile.Lighting == OneXLightingKind.HidV1X2 ? [1, 2, 7] : [0];
    }

    private IReadOnlyList<byte> SecondarySides()
    {
        return profile.Lighting switch
        {
            OneXLightingKind.Serial or OneXLightingKind.HidV1Secondary => [3, 4],
            OneXLightingKind.HidV1X2 => [5, 6], _ => []
        };
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_ec is not null)
        {
            if (profile.X2 || profile.Three)
            {
                Read("fan.measured-duty", profile.FanDuty,
                    raw => CapabilityValue.Integer(DecodeDuty(profile, raw)));
            }

            if (profile.Temperature)
            {
                Read(CapabilityIds.Temperature, 0x470,
                    raw => raw is > 0 and <= 110 ? CapabilityValue.Integer(raw) : null);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal override ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        return RefreshAsync(token);
    }

    private void Read(string id, ushort address, Func<byte, CapabilityValue?> convert)
    {
        try
        {
            if (convert(_ec!.ReadByte(address)) is { } value)
            {
                Observe(id, null, value);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _observed.Remove((id, null));
            _observedAt.Remove((id, null));
        }
    }

    private void Observe(string id, string? instance, CapabilityValue value)
    {
        _observed[(id, instance)] = value;
        _observedAt[(id, instance)] = DateTimeOffset.UtcNow;
    }

    private bool ReadChargeFirmwareSupport()
    {
        try
        {
            using var query = new ManagementObjectSearcher("root\\CIMV2",
                "SELECT EmbeddedControllerMajorVersion,EmbeddedControllerMinorVersion FROM Win32_BIOS");
            using var result = query.Get();
            foreach (ManagementObject bios in result)
            {
                using (bios)
                {
                    var major = Convert.ToInt32(bios["EmbeddedControllerMajorVersion"], CultureInfo.InvariantCulture);
                    var minor = Convert.ToInt32(bios["EmbeddedControllerMinorVersion"], CultureInfo.InvariantCulture);
                    if (major == 255 || minor == 255)
                    {
                        return false;
                    }

                    return profile.Charge == OneXChargeKind.Amd ? major >= 1 && minor >= 3 : major >= 0 && minor >= 67;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        return false;
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            if (_journal is not null)
            {
                foreach (var entry in _journal.Entries<OneXRegisterState>().Reverse())
                {
                    await RestoreAsync(entry.ServiceId, entry.OriginalState, token).ConfigureAwait(false);
                }
            }

            if (_takeoverActive && profile.Ec == OneXEcKind.Wmi)
            {
                token.ThrowIfCancellationRequested();
                WriteTakeoverBit(_takeoverEc!, false);
                _takeoverActive = false;
            }

            return _unknownOriginal || _journal?.Entries<OneXRegisterState>().Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The original EC state could not be confirmed or restored."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            if (!ReferenceEquals(_takeoverEc, _ec))
            {
                _takeoverEc?.Dispose();
            }

            _takeoverEc = null;
            _ec?.Dispose();
            _ec = null;
            _lights?.Dispose();
            _lights = null;
            _vibration?.Dispose();
            _vibration = null;
        }
    }

    private async ValueTask RestoreAsync(string id, OneXRegisterState original, CancellationToken token)
    {
        if (_journal!.EntryFor<OneXRegisterState>(id)?.FirmwareIdentity != context.Firmware)
        {
            await _journal.RetireAsync(id, "firmware changed", token).ConfigureAwait(false);
            return;
        }

        try
        {
            var transport = original.Acpi ? _takeoverEc ??= OpenAcpi() : _ec;
            if (transport is null)
            {
                throw new IOException("The original register transport is unavailable.");
            }

            for (var index = 0; index < original.Addresses.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (id == "takeover" && profile.Ec == OneXEcKind.Wmi && original.Addresses[index] == 0x04EB)
                {
                    if (original.UnknownOriginal)
                    {
                        throw new IOException("The original takeover bit was not captured.");
                    }

                    WriteTakeoverBit(transport, (original.Values[index] & 0x40) != 0);
                    _takeoverActive = false;
                }
                else
                {
                    transport.WriteByte(original.Addresses[index], original.Values[index]);
                }
            }

            await _journal!.SetStatusAsync(id, DeviceRecoveryStatus.RestoredUnverified, token).ConfigureAwait(false);
        }
        catch
        {
            await _journal!.SetStatusAsync(id, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private IReadOnlyList<CapabilityDescriptor> Describe()
    {
        List<CapabilityDescriptor> descriptors =
        [
            Choice(CapabilityIds.FanMode, CapabilityRole.FanMode, "Fan mode", ["automatic", "custom", "full-speed"],
                false),
            Range("fan.duty", CapabilityRole.FanDuty, "Fan speed", 0, 100, CapabilityUnit.Percent, true, false)
        ];
        if (profile.X2 || profile.Three)
        {
            descriptors.Add(Range("fan.measured-duty", CapabilityRole.Telemetry, "Measured fan duty", 0, 100,
                CapabilityUnit.Percent, false, false));
        }
        if (profile.Temperature)
        {
            descriptors.Add(Range(CapabilityIds.Temperature, CapabilityRole.Telemetry, "CPU temperature", 0, 110,
                CapabilityUnit.Celsius, false, false));
        }

        if (_chargeSupported)
        {
            descriptors.Add(Range(CapabilityIds.ChargeLimit, CapabilityRole.ChargeLimit, "Charge limit", 0, 100,
                CapabilityUnit.Percent, true, true));
            descriptors.Add(Choice("battery.bypass-charging", CapabilityRole.ChargeBypass, "Bypass charging",
                ["disabled", "until-sleep", "always"], true));
        }

        if (profile.Lighting != OneXLightingKind.None)
        {
            descriptors.Add(Range(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                    "Lighting brightness", 0, 100, CapabilityUnit.Percent, true, true) with
                {
                    SupportsRead = false
                });
            var effects = profile.Lighting == OneXLightingKind.HidV1X2
                ? new[] { "off", "solid", "breathing" }
                : profile.Lighting == OneXLightingKind.HidV2 || profile.Lighting == OneXLightingKind.HidV1G1
                    ? ["off", "solid", "rainbow"]
                    :
                    [
                        "off", "solid", "rainbow", "monster-woke", "sunset", "neon", "dreamy", "cyberpunk", "colorful",
                        "classic", "aurora", "sun"
                    ];
            if (profile.Lighting == OneXLightingKind.Serial)
            {
                effects = [.. effects, "aok-classic"];
            }

            descriptors.Add(Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect, "Lighting effect",
                    effects, true) with
                {
                    SupportsRead = false
                });
            descriptors.Add(Color("primary", "Controller lighting"));
            if (SecondarySides().Count != 0)
            {
                descriptors.Add(Color("secondary", "Rear lighting"));
            }
        }

        if (profile.Vibration)
        {
            descriptors.Add(Range("haptic.strength", CapabilityRole.GenericRange, "Rumble strength", 0, 10,
                    CapabilityUnit.None, true, true) with
                {
                    SupportsRead = false
                });
        }

        return Array.AsReadOnly(descriptors.ToArray());
    }

    internal static int DecodeDuty(OneXProtocolProfile profile, byte raw)
    {
        return Math.Clamp((int)Math.Round((raw - profile.MinimumDuty) * 100d /
            (profile.MaximumDuty - profile.MinimumDuty)), 0, 100);
    }

    private IEmbeddedController OpenAcpi()
    {
        return openAcpi?.Invoke() ?? PawnIoEmbeddedController.OpenAcpi();
    }


    private static CapabilityDescriptor Color(string zone, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor, InstanceId = zone, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color, Display = CapabilityDescriptors.Label(label), SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private static CapabilityDescriptor Range(string id, CapabilityRole role, string label, int minimum, int maximum,
        CapabilityUnit unit, bool write, bool persistent)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, Display = CapabilityDescriptors.Label(label),
            ValueKind = CapabilityValueKind.Integer,
            Minimum = minimum,
            Maximum = maximum, Step = 1, Unit = unit, SupportsRead = true, SupportsWrite = write,
            Persistence = persistent ? CapabilityPersistence.DevicePersistent : CapabilityPersistence.Volatile
        };
    }

    private static CapabilityDescriptor Choice(string id, CapabilityRole role, string label, string[] choices,
        bool persistent)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, Display = CapabilityDescriptors.Label(label),
            ValueKind = CapabilityValueKind.Choice,
            SupportsRead = true,
            SupportsWrite = true,
            Persistence = persistent ? CapabilityPersistence.DevicePersistent : CapabilityPersistence.Volatile,
            Choices = Array.AsReadOnly(choices
                .Select(value => new CapabilityChoice(value, CapabilityDescriptors.Label(value))).ToArray())
        };
    }
}

internal sealed record OneXRegisterState
{
    public required ushort[] Addresses { get; init; }
    public required byte[] Values { get; init; }
    public bool Acpi { get; init; }
    public bool UnknownOriginal { get; init; }
}
