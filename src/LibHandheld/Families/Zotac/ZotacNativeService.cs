using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Zotac;

/// <summary>Zone vendor lighting, volatile M1/M2 acquisition and the firmware UMA allocation setting.</summary>
internal sealed partial class ZotacNativeService(ProfiledFeatureContext context)
    : ProfiledFeatureService("zotac-controls")
{
    private static readonly IReadOnlyList<CapabilityDescriptor> Lighting =
    [
        Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect, "Lighting effect",
            ["off", "solid", "breathing", "rainbow", "gradient"]),
        Range(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness, "Lighting brightness", 0, 100, 25),
        Range(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed, "Lighting speed", 0, 100, 1),
        Color("main", "Main lighting color"), Color("secondary", "Secondary lighting color")
    ];

    private readonly Dictionary<(string, string?), CapabilityValue> _observed = [];
    private ZotacCommandTransport? _hid;
    private ZotacRecoveryJournal? _journal;
    private bool _manage = context.ControllerManagementEnabled;
    private uint _ram;
    private bool _rearChanged;
    private ManagementObject? _uma;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => Array.AsReadOnly(Lighting.Concat(new[]
    {
        Range("firmware.vram-addition", CapabilityRole.GenericRange, "Additional reserved video memory (GB)", 0, 12,
                1) with
            {
                ApplyTiming = CapabilityApplyTiming.SystemRestart, ProfileScope = CapabilityProfileScope.GlobalOnly
            }
    }).ToArray());

    internal override IReadOnlyList<CapabilityState> States => Array.AsReadOnly(Descriptors.Select(descriptor =>
    {
        var available = descriptor.CapabilityId == "firmware.vram-addition" ? _uma is not null : _hid is not null;
        _observed.TryGetValue((descriptor.CapabilityId, descriptor.InstanceId), out var value);
        return new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
            Available = available, ObservedValue = value,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
            DescriptorGeneration = 0, CycleGeneration = 0, ObservedAt = value is null ? null : DateTimeOffset.UtcNow
        };
    }).ToArray());

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _hid = ZotacCommandTransport.Open();
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM UMAInterface");
            using var rows = searcher.Get();
            _uma = rows.Cast<ManagementObject>().FirstOrDefault();
        }
        catch (ManagementException)
        {
        }

        if (GetPhysicallyInstalledSystemMemory(out var kilobytes))
        {
            _ram = checked((uint)(kilobytes / 1048576));
        }

        if (_ram is not (16 or 32 or 64))
        {
            _ram = 16;
        }

        _journal = await ZotacRecoveryJournal.Open(Path.Combine(context.StateDirectory, "zotac-controls"), token)
            .ConfigureAwait(false);
        if (_hid is not null)
        {
            var prior = _journal.EntryFor("rear-profile");
            if (prior is not null && prior.FirmwareIdentity != Binding())
            {
                await _journal.SetStatusAsync("rear-profile", DeviceRecoveryStatus.RestoredVerified, token)
                    .ConfigureAwait(false);
            }
            else if (prior?.Status == DeviceRecoveryStatus.Pending)
            {
                await RestoreRearAsync(token).ConfigureAwait(false);
            }

            if (_manage && _journal.EntryFor("rear-profile") is null)
            {
                await RemapRearAsync(token).ConfigureAwait(false);
            }
        }

        return Set(_hid is not null || _uma is not null ? DeviceServiceState.Owned : DeviceServiceState.Passive,
            _hid is null && _uma is null ? Missing("The Zone vendor HID and UMA WMI providers are absent.") : null);
    }

    internal override async ValueTask SetControllerManagementAsync(bool enabled, CancellationToken token)
    {
        _manage = enabled;
        if (_hid is null)
        {
            return;
        }

        if (enabled)
        {
            await RemapRearAsync(token).ConfigureAwait(false);
        }
        else
        {
            await RestoreRearAsync(token).ConfigureAwait(false);
        }
    }

    private string Binding()
    {
        return $"{context.Firmware}|{_hid!.Firmware}";
    }

    private async ValueTask RemapRearAsync(CancellationToken token)
    {
        await _journal!.BeginAsync("rear-profile", Binding(), new ZotacRecoveryState(), token).ConfigureAwait(false);
        _rearChanged = true; // Record uncertainty before the first report can leave this process.
        _hid!.Write(ZotacProtocol.Rear(1), token);
        _hid.Write(ZotacProtocol.Rear(2), token);
    }

    private async ValueTask RestoreRearAsync(CancellationToken token)
    {
        if (_hid is null || _journal?.PendingOriginalFor("rear-profile") is null)
        {
            return;
        }

        try
        {
            _hid.Write(ZotacProtocol.Command(0xF1), token);
            _rearChanged = false;
            await _journal
                .SetStatusAsync("rear-profile", DeviceRecoveryStatus.RestoredUnverified, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            await _journal.SetStatusAsync("rear-profile", DeviceRecoveryStatus.RestoreFailed, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        var dispatched = false;
        try
        {
            if (command.CapabilityId == "firmware.vram-addition")
            {
                if (_uma is null)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The Zone UMAInterface WMI provider is absent.");
                }

                using var input = _uma.GetMethodParameters("SetEcValue");
                input["Index"] = (ushort)122;
                input["Value"] = EncodeVram(_ram, checked((uint)command.RequestedValue!.IntegerValue!.Value));
                token.ThrowIfCancellationRequested();
                dispatched = true;
                using var output = _uma.InvokeMethod("SetEcValue", input, null);
                _observed[(command.CapabilityId, command.InstanceId)] = command.RequestedValue!;
                return CommandResults.Unverified(command);
            }

            if (_hid is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                    "The Zone vendor HID collection is absent.");
            }

            // SaveConfigData would otherwise persist our temporary M1/M2 remap along with lighting.
            var restoreRear = _rearChanged;
            if (restoreRear)
            {
                var binding = _hid.Firmware;
                await RestoreRearAsync(token).ConfigureAwait(false);
                await ReopenAfterReloadAsync(binding, token).ConfigureAwait(false);
            }

            var value = command.RequestedValue!;
            token.ThrowIfCancellationRequested();
            dispatched = true;
            switch (command.CapabilityId)
            {
                case CapabilityIds.LightingEffect:
                    _hid.Write(ZotacProtocol.Command(0xAD, 2, value.ChoiceValue switch
                    {
                        "off" => 240, "solid" => 4, "breathing" => 1, "rainbow" => 0, "gradient" => 2,
                        _ => throw new ArgumentOutOfRangeException(nameof(command))
                    }), token); break;
                case CapabilityIds.LightingBrightness:
                    _hid.Write(ZotacProtocol.Command(0xAD, 3, checked((byte)value.IntegerValue!.Value)), token); break;
                case CapabilityIds.LightingSpeed:
                    _hid.Write(
                        ZotacProtocol.Command(0xAD, 1,
                            value.IntegerValue <= 33 ? (byte)0 : value.IntegerValue <= 66 ? (byte)1 : (byte)2),
                        token); break;
                case CapabilityIds.LightingColor:
                    _hid.Write(
                        ZotacProtocol.Color(command.InstanceId == "secondary" ? (byte)1 : (byte)0,
                            value.ColorValue!.Value), token); break;
                default:
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "Unknown Zone vendor command.");
            }

            // HC's save command deliberately has no CRC bytes.
            var save = new byte[65];
            save[1] = 0xE1;
            save[4] = 60;
            save[5] = 0xFB;
            _hid.Write(save, token);
            _observed[(command.CapabilityId, command.InstanceId)] = value;
            if (_manage && restoreRear)
            {
                await RemapRearAsync(token).ConfigureAwait(false);
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

    private async ValueTask ReopenAfterReloadAsync(string binding, CancellationToken token)
    {
        _hid?.Dispose();
        _hid = null;
        var expires = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < expires)
        {
            token.ThrowIfCancellationRequested();
            var candidate = ZotacCommandTransport.Open();
            if (candidate is not null)
            {
                if (candidate.Firmware == binding)
                {
                    _hid = candidate;
                    return;
                }

                candidate.Dispose();
            }

            await Task.Delay(50, token).ConfigureAwait(false);
        }

        throw new IOException(
            "The same Zone controller did not reappear after its profile reload; no command was repeated.");
    }

    internal static byte EncodeVram(uint ram, uint addition)
    {
        return unchecked((byte)((ram + addition) * 4));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            await RestoreRearAsync(token).ConfigureAwait(false);
            return _journal?.OutstandingEntries.Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("The saved Zone profile reload was accepted without a mapping readback."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            _hid?.Dispose();
            _hid = null;
            _uma?.Dispose();
            _uma = null;
            _observed.Clear();
        }
    }

    private static CapabilityDisplay Label(string text)
    {
        return new CapabilityDisplay { Key = DisplayKey.Custom, CustomLabel = text };
    }

    private static CapabilityDescriptor Range(string id, CapabilityRole role, string label, int minimum, int maximum,
        int step)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Integer, Display = Label(label),
            Minimum = minimum,
            Maximum = maximum, Step = step, SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private static CapabilityDescriptor Choice(string id, CapabilityRole role, string label, string[] choices)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Choice, Display = Label(label),
            SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent,
            Choices = Array.AsReadOnly(choices.Select(value => new CapabilityChoice(value, Label(value))).ToArray())
        };
    }

    private static CapabilityDescriptor Color(string instance, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor, InstanceId = instance, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = Label(label), SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
}

internal sealed record ZotacRecoveryState;

internal sealed class ZotacRecoveryJournal()
    : DeviceRecoveryJournal<ZotacRecoveryState>(ZotacRecoveryJson.Default.RecoveryDocument)
{
    internal static async ValueTask<ZotacRecoveryJournal> Open(string directory, CancellationToken token)
    {
        var journal = new ZotacRecoveryJournal();
        await journal.LoadAsync(directory, token).ConfigureAwait(false);
        return journal;
    }

    protected override void ValidateEntry(DeviceRecoveryEntry<ZotacRecoveryState> entry)
    {
        if (entry.ServiceId != "rear-profile")
        {
            throw new InvalidDataException("Unknown Zone recovery operation.");
        }
    }
}

[JsonSerializable(typeof(DeviceRecoveryDocument<ZotacRecoveryState>), TypeInfoPropertyName = "RecoveryDocument")]
internal partial class ZotacRecoveryJson : JsonSerializerContext;
