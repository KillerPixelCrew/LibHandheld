using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.SteamDeck;

internal readonly record struct DeckFirmware(ushort Version, byte Board, bool Oled, bool ChargeLimit)
{
    internal static DeckFirmware? Match(ushort firmware, byte board, byte pdcs = 0)
    {
        if (pdcs != 0)
        {
            return null;
        }

        return (firmware, board) switch
        {
            (0xB030, 6) => new DeckFirmware(firmware, board, false, false),
            (0xB030, 10) => new DeckFirmware(firmware, board, false, true),
            (0x1010 or 0x1050 or 0x1090 or 0x1100, 5) => new DeckFirmware(firmware, board, true, true),
            (0x1030, 5) => new DeckFirmware(firmware, board, true, false),
            _ => null
        };
    }

    internal float BatteryTemperature(ushort raw)
    {
        return ((Oled ? raw : BinaryPrimitives.ReverseEndianness(raw)) - 0x0AAC) / 10f;
    }
}

internal sealed partial class SteamDeckNativeService
{
    private const uint FanTarget = 0xFE700B92;
    private const uint FanGain = 0xFE700B95;
    private const uint FanRamp = 0xFE700B97;
    private const uint FanRpm = 0xFE7003B0;
    private const uint BatteryTemperature = 0xFE70046E;
    private const uint ChargeLimit = 0xFE700B9F;
    private const int MaximumFanRpm = 0x1C84;

    private static readonly CapabilityDescriptor[] HardwareDescriptors =
    [
        new()
        {
            CapabilityId = "fan.mode", Role = CapabilityRole.FanMode, ValueKind = CapabilityValueKind.Choice,
            Display = CapabilityDescriptors.Label("Fan mode"), SupportsWrite = true,
            Persistence = CapabilityPersistence.Volatile,
            Choices = Array.AsReadOnly(new[] { "automatic", "custom", "full-speed" }
                .Select(mode => new CapabilityChoice(mode, CapabilityDescriptors.Label(mode))).ToArray())
        },
        Range("fan.duty", CapabilityRole.FanDuty, "Target fan speed", 0, 100, CapabilityUnit.Percent, true),
        Range("fan.target-rpm", CapabilityRole.FanTargetRpm, "Target fan RPM", 0, MaximumFanRpm, CapabilityUnit.Rpm,
            true),
        Range("fan.rpm", CapabilityRole.FanMeasuredRpm, "Fan RPM", 0, 65535, CapabilityUnit.Rpm, false),
        Range("charge.limit", CapabilityRole.ChargeLimit, "Battery charge ceiling", 0, 100, CapabilityUnit.Percent,
            true),
        Range("battery.measured-charge-ceiling", CapabilityRole.Telemetry, "Measured charge ceiling", 0, 100,
            CapabilityUnit.Percent, false),
        Range("battery.temperature", CapabilityRole.Telemetry, "Battery temperature", -40, 150, CapabilityUnit.Celsius,
            false)
    ];

    private int? _batteryTemperature;
    private DateTimeOffset? _chargeAt;
    private int? _chargeLimit;
    private int? _measuredChargeLimit;
    private DateTimeOffset? _measuredChargeAt;
    private string? _fanMode;
    private int? _fanRpm;
    private int? _fanTarget;
    private DeckFirmware? _firmware;
    private string? _hardwareIdentity;
    private CapabilityReason? _hardwareReason;
    private ISteamDeckMemory? _memory;
    private DateTimeOffset? _modeAt;
    private DateTimeOffset? _rpmAt;
    private DateTimeOffset? _targetAt;
    private DateTimeOffset? _temperatureAt;

    private async ValueTask AcquireHardwareAsync(CancellationToken token)
    {
        if (_memory is not null)
        {
            return;
        }

        try
        {
            _memory = openMemory is null ? SteamDeckMemory.Open(context.Resources.InpOutPath) : openMemory();
            var firmware = checked((ushort)_memory.Read(0xFE700C4C, 16));
            var board = checked((byte)_memory.Read(0xFE7003BD, 8));
            var pdcs = checked((byte)_memory.Read(0xFE700C01, 8));
            _firmware = DeckFirmware.Match(firmware, board, pdcs)
                        ?? throw new NotSupportedException(
                            $"Valve EC firmware 0x{firmware:X4}, board 0x{board:X2} is not supported by the documented interface.");
            _hardwareIdentity = context.Firmware + $"|ValveEC:{firmware:X4}:{board:X2}:{pdcs:X2}";
            await RestoreHardwareAsync(token).ConfigureAwait(false);
            _hardwareReason = null;
            RefreshHardware();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _memory?.Dispose();
            _memory = null;
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _hardwareReason = Missing(exception.Message);
            _memory?.Dispose();
            _memory = null;
            _firmware = null;
        }
    }

    private void RefreshHardware()
    {
        if (_memory is null || _firmware is null)
        {
            return;
        }

        try
        {
            HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
            {
                _fanRpm = checked((int)_memory.Read(FanRpm, 16));
                _rpmAt = DateTimeOffset.UtcNow;
                _batteryTemperature =
                    (int)Math.Round(
                        _firmware.Value.BatteryTemperature(checked((ushort)_memory.Read(BatteryTemperature, 16))));
                _temperatureAt = DateTimeOffset.UtcNow;
                return true;
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _hardwareReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                "Valve EC observation is unavailable; write support is retained.");
        }
    }

    private bool RefreshChargeMeasurement()
    {
        _measuredChargeLimit = null;
        _measuredChargeAt = null;
        if (_memory is null || _firmware?.ChargeLimit != true)
        {
            return true;
        }

        try
        {
            var raw = HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2),
                () => _memory.Read(ChargeLimit, 8));
            if (raw > 100)
            {
                return false;
            }

            _measuredChargeLimit = (int)raw;
            _measuredChargeAt = DateTimeOffset.UtcNow;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private async ValueTask<CapabilityCommandResult> ExecuteHardwareAsync(CapabilityCommand command,
        CancellationToken token)
    {
        if (_memory is null || _firmware is null || _journal is null || _hardwareIdentity is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The exact Valve EC firmware and native driver must be acquired.");
        }

        var dispatched = false;
        try
        {
            switch (command.CapabilityId)
            {
                case "fan.mode":
                case "fan.duty":
                case "fan.target-rpm":
                    var mode = command.CapabilityId == "fan.mode" ? command.RequestedValue!.ChoiceValue! : "custom";
                    var target = command.CapabilityId switch
                    {
                        "fan.duty" => checked((int)Math.Round(command.RequestedValue!.IntegerValue!.Value *
                            MaximumFanRpm / 100d)),
                        "fan.target-rpm" => command.RequestedValue!.IntegerValue,
                        _ => mode == "full-speed" ? MaximumFanRpm : _fanTarget
                    };
                    await _journal.BeginAsync("deck-fan", _hardwareIdentity,
                        () => HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2),
                            () => new SteamDeckOriginal
                            {
                                Addresses = [FanTarget, FanGain, FanRamp],
                                Memory =
                                [
                                    ReadOriginalMemory(FanTarget, 16), ReadOriginalMemory(FanGain, 16),
                                    ReadOriginalMemory(FanRamp, 16)
                                ]
                            }), token).ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
                    {
                        _memory.Write(FanGain, 10, 16);
                        _memory.Write(FanRamp, mode == "automatic" ? 20u : 10u, 16);
                        _memory.WriteFanMode(mode == "automatic" ? (byte)0xCD : (byte)0xCC);
                        if (mode != "automatic" && target is { } rpm)
                        {
                            _memory.Write(FanTarget, checked((uint)Math.Clamp(rpm, 0, MaximumFanRpm)), 16);
                        }

                        return true;
                    });
                    _fanMode = mode;
                    _fanTarget = target;
                    _modeAt = DateTimeOffset.UtcNow;
                    _targetAt = DateTimeOffset.UtcNow;
                    break;
                case "charge.limit":
                    if (!_firmware.Value.ChargeLimit)
                    {
                        return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                            "This Valve EC firmware has no charge-ceiling support.");
                    }

                    await _journal.BeginAsync("deck-charge", _hardwareIdentity,
                            () => new SteamDeckOriginal
                                { Addresses = [ChargeLimit], Memory = [ReadOriginalMemory(ChargeLimit, 32)] }, token)
                        .ConfigureAwait(false);

                    token.ThrowIfCancellationRequested();
                    dispatched = true;
                    _memory.Write(ChargeLimit, checked((uint)command.RequestedValue!.IntegerValue!.Value), 32);
                    _chargeLimit = command.RequestedValue.IntegerValue;
                    _chargeAt = DateTimeOffset.UtcNow;
                    break;
                default:
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "This capability is published through its native input stream.");
            }

            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted,
                    exception.Message)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    private async ValueTask RestoreHardwareAsync(CancellationToken token)
    {
        if (_memory is null || _journal is null)
        {
            return;
        }

        foreach (var id in new[] { "deck-fan", "deck-charge" })
        {
            if (_journal.EntryFor<SteamDeckOriginal>(id) is not { } entry)
            {
                continue;
            }

            if (entry.FirmwareIdentity != _hardwareIdentity)
            {
                await _journal.RetireAsync(id, "firmware changed", token).ConfigureAwait(false);
                continue;
            }

            try
            {
                HardwareBus.Run(HardwareBus.IndexedIo, TimeSpan.FromSeconds(2), () =>
                {
                    if (id == "deck-fan")
                    {
                        _memory.WriteFanMode(0xCD);
                    }

                    for (var i = 0; i < entry.OriginalState.Addresses.Length; i++)
                    {
                        if (entry.OriginalState.Memory[i] == uint.MaxValue)
                        {
                            continue;
                        }

                        _memory.Write(entry.OriginalState.Addresses[i], entry.OriginalState.Memory[i],
                            id == "deck-charge" ? 32 : 16);
                    }

                    return true;
                });
                await _journal.SetStatusAsync(id,
                        DeviceRecoveryStatus.RestoredUnverified,
                        token)
                    .ConfigureAwait(false);
            }
            catch
            {
                await _journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }
    }

    private async ValueTask ReleaseHardwareAsync(CancellationToken token)
    {
        try
        {
            await RestoreHardwareAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _memory?.Dispose();
            _memory = null;
            _fanRpm = null;
            _fanTarget = null;
            _chargeLimit = null;
            _measuredChargeLimit = null;
            _measuredChargeAt = null;
            _batteryTemperature = null;
            _fanMode = null;
            _rpmAt = null;
            _targetAt = null;
            _chargeAt = null;
            _temperatureAt = null;
            _modeAt = null;
        }
    }

    private uint ReadOriginalMemory(uint address, int width)
    {
        try
        {
            return _memory!.Read(address, width);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return uint.MaxValue;
        }
    }

    private IEnumerable<CapabilityState> HardwareStates()
    {
        foreach (var descriptor in HardwareDescriptors)
        {
            var available = _memory is not null &&
                            (descriptor.Role != CapabilityRole.ChargeLimit &&
                             descriptor.CapabilityId != "battery.measured-charge-ceiling" ||
                             _firmware?.ChargeLimit == true);
            var value = descriptor.CapabilityId switch
            {
                "fan.mode" => _fanMode is null ? null : CapabilityValue.Choice(_fanMode),
                "fan.duty" => _fanTarget is { } target
                    ? CapabilityValue.Integer(Math.Clamp((int)Math.Round(target * 100d / MaximumFanRpm), 0, 100))
                    : null,
                "fan.target-rpm" => _fanTarget is { } rpm
                    ? CapabilityValue.Integer(Math.Clamp(rpm, 0, MaximumFanRpm))
                    : null,
                "fan.rpm" => _fanRpm is { } measured ? CapabilityValue.Integer(measured) : null,
                "charge.limit" => _chargeLimit is { } charge ? CapabilityValue.Integer(charge) : null,
                "battery.measured-charge-ceiling" => _measuredChargeLimit is { } ceiling
                    ? CapabilityValue.Integer(ceiling) : null,
                "battery.temperature" => _batteryTemperature is { } temp ? CapabilityValue.Integer(temp) : null,
                _ => null
            };
            yield return new CapabilityState
            {
                CapabilityId = descriptor.CapabilityId, Available = available,
                Reason = available ? null : _hardwareReason ?? Missing("This firmware has no charge-ceiling support."),
                ObservedValue = value,
                Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                ObservedAt = value is null
                    ? null
                    : descriptor.CapabilityId switch
                    {
                        "fan.mode" => _modeAt, "fan.duty" or "fan.target-rpm" => _targetAt, "fan.rpm" => _rpmAt,
                        "charge.limit" => _chargeAt, "battery.measured-charge-ceiling" => _measuredChargeAt,
                        "battery.temperature" => _temperatureAt, _ => null
                    }
            };
        }
    }

    private static CapabilityDescriptor Range(string id, CapabilityRole role, string label, int minimum, int maximum,
        CapabilityUnit unit, bool write)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Integer,
            Display = CapabilityDescriptors.Label(label),
            Minimum = minimum, Maximum = maximum, Step = 1, Unit = unit, SupportsRead = true, SupportsWrite = write,
            Persistence = CapabilityPersistence.Volatile
        };
    }
}
