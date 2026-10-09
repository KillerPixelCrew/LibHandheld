using System.Management;
using System.Runtime.InteropServices;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

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

    private readonly ObservedValues _observed = new();
    private ZotacCommandTransport? _hid;
    private DeviceRecoveryJournal? _journal;
    private bool _manage = context.ControllerManagementEnabled;
    private uint _ram;
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
        var available = descriptor.CapabilityId == "firmware.vram-addition"
            ? _uma is not null && _ram < 64
            : State == DeviceServiceState.Owned;
        var value = _observed.Value(descriptor.CapabilityId, descriptor.InstanceId);
        return new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
            Available = available, ObservedValue = value,
            Reason = descriptor.CapabilityId == "firmware.vram-addition" && _ram >= 64
                ? new CapabilityReason(CapabilityReasonCode.Unsupported,
                    "The 8-bit UMA encoding cannot represent a 64 GB base allocation.")
                : null,
            Quality = value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
            ObservedAt = _observed.Time(descriptor.CapabilityId, descriptor.InstanceId)
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
            _uma = WmiProvider.TryOpen("root\\WMI", "SELECT * FROM UMAInterface");
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

        _journal = context.Journal;
        if (_hid is not null)
        {
            var prior = _journal.EntryFor<ZotacRecoveryState>("rear-profile");
            if (prior is not null)
            {
                await _journal.ReconcileAsync<ZotacRecoveryState>("rear-profile", Binding(),
                    (_, ct) => RestoreRearAsync(ct), token).ConfigureAwait(false);
            }


            if (_manage)
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
        _hid!.Write(ZotacProtocol.Rear(1), token);
        _hid.Write(ZotacProtocol.Rear(2), token);
    }

    private async ValueTask RestoreRearAsync(CancellationToken token)
    {
        if (_hid is null || _journal?.PendingOriginalFor<ZotacRecoveryState>("rear-profile") is null)
        {
            return;
        }

        if (_journal!.EntryFor<ZotacRecoveryState>("rear-profile")?.FirmwareIdentity != Binding())
        {
            await _journal.RetireAsync("rear-profile", "firmware changed", token).ConfigureAwait(false);
            return;
        }

        try
        {
            _hid.Write(ZotacProtocol.Command(0xF1), token);
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

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        return ValueTask.FromResult(ExecuteCore(command, token));
    }

    private CapabilityCommandResult ExecuteCore(CapabilityCommand command, CancellationToken token)
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
                using var output = WmiProvider.Invoke(_uma, "SetEcValue", input);
                _observed.Written(command.CapabilityId, command.RequestedValue!, command.InstanceId);
                return CommandResults.Applied(command);
            }

            _hid ??= ZotacCommandTransport.Open();
            if (_hid is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                    "The Zone vendor HID collection is absent.");
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
            _observed.Written(command.CapabilityId, value, command.InstanceId);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (command.CapabilityId != "firmware.vram-addition")
            {
                _hid?.Dispose();
                _hid = null;
            }

            return dispatched
                ? CommandResults.Indeterminate(command, CapabilityReasonCode.TransportFaulted, exception.Message)
                : CommandResults.Rejected(command,
                    exception is ArgumentOutOfRangeException
                        ? CapabilityReasonCode.ValueOutOfRange
                        : CapabilityReasonCode.TransportFaulted, exception.Message);
        }
    }

    internal static byte EncodeVram(uint ram, uint addition)
    {
        if (ram is not (16 or 32 or 64) || addition > 12 || (ulong)(ram + addition) * 4 > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(addition),
                "The requested UMA allocation does not fit the firmware's 8-bit encoding.");
        }

        return checked((byte)((ram + addition) * 4));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            await RestoreRearAsync(token).ConfigureAwait(false);
            return _journal?.Entries<ZotacRecoveryState>().Count > 0
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


    internal override ValueTask DisposeAsync()
    {
        _hid?.Dispose();
        _hid = null;
        _uma?.Dispose();
        _uma = null;
        _observed.Clear();
        return ValueTask.CompletedTask;
    }

    private static CapabilityDescriptor Range(string id, CapabilityRole role, string label, int minimum, int maximum,
        int step)
    {
        return CapabilityDescriptors.Integer(id, role, CapabilityDescriptors.Label(label), minimum, maximum,
            CapabilityUnit.None, false, true, CapabilityPersistence.DevicePersistent, step);
    }

    private static CapabilityDescriptor Choice(string id, CapabilityRole role, string label, string[] choices)
    {
        return CapabilityDescriptors.Choice(id, role, CapabilityDescriptors.Label(label),
            CapabilityDescriptors.Choices(choices), false, true, CapabilityPersistence.DevicePersistent);
    }

    private static CapabilityDescriptor Color(string instance, string label)
    {
        return CapabilityDescriptors.Color(CapabilityIds.LightingColor, instance, CapabilityDescriptors.Label(label),
            false,
            CapabilityPersistence.DevicePersistent);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
}

internal sealed record ZotacRecoveryState;
