using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Gpd;

/// <summary>Preserves WinControls' durable table while publishing only acknowledged user commands.</summary>
internal sealed class GpdNativeService()
    : ProfiledFeatureService("gpd-controls")
{
    private readonly ObservedValues _observed = new();
    private GpdControlsProtocol? _protocol;
    private GpdControlsSnapshot? _snapshot;
    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _snapshot is null ? [] : Describe(_snapshot);

    internal override IReadOnlyList<CapabilityState> States => _snapshot is null
        ? []
        : Descriptors.Select(descriptor => new CapabilityState
        {
            CapabilityId = descriptor.CapabilityId, Available = State == DeviceServiceState.Owned,
            ObservedValue = _observed.Value(descriptor.CapabilityId),
            Quality = _observed.Value(descriptor.CapabilityId) is null
                ? HardwareStateQuality.Unknown
                : HardwareStateQuality.Observed,
            ObservedAt = _observed.Time(descriptor.CapabilityId)
        }).ToArray();

    public override bool Suspendable => true;

    public override ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        _protocol = GpdControlsProtocol.Open();
        if (_protocol is null)
        {
            return ValueTask.FromResult(Set(DeviceServiceState.Passive,
                Missing("The GPD WinControls collection is not present.")));
        }

        try
        {
            _snapshot = _protocol.Read(token);
        }
        catch
        {
            _protocol.Dispose();
            _protocol = null;
            throw;
        }

        return ValueTask.FromResult(Set(DeviceServiceState.Owned));
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (State == DeviceServiceState.Owned)
        {
            _protocol ??= GpdControlsProtocol.Open();
        }

        if (_protocol is null || _snapshot is null || State != DeviceServiceState.Owned)
        {
            return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "GPD WinControls is unavailable."));
        }

        var dispatched = false;
        try
        {
            // These bytes are required to preserve unrelated EEPROM fields during the whole-table
            // transaction. They are transaction inputs, never readbacks of control state.
            var snapshot = _protocol.Read(token);
            var next = (byte[])snapshot.Configuration.Clone();
            Apply(next, snapshot.Version2, command);
            if (next.AsSpan().SequenceEqual(snapshot.Configuration))
            {
                _observed.Written(command.CapabilityId, command.RequestedValue!);
                return ValueTask.FromResult(CommandResults.Applied(command));
            }

            dispatched = true;
            _protocol.Write(snapshot, next, token);
            _snapshot = snapshot with { Configuration = next };
            _observed.Written(command.CapabilityId, command.RequestedValue!);
            return ValueTask.FromResult(CommandResults.Applied(command));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _protocol?.Dispose();
            _protocol = null;
            return ValueTask.FromResult(dispatched
                ? CommandResults.Indeterminate(command,
                    CapabilityReasonCode.TransportFaulted, exception.Message)
                : CommandResults.Rejected(command, CapabilityReasonCode.TransportFaulted, exception.Message));
        }
    }

    public override ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        DisposeHandles();
        return ValueTask.FromResult(Set(DeviceServiceState.Idle));
    }

    internal override ValueTask DisposeAsync()
    {
        DisposeHandles();
        return ValueTask.CompletedTask;
    }

    private void DisposeHandles()
    {
        _protocol?.Dispose();
        _protocol = null;
        _snapshot = null;
        _observed.Clear();
    }

    private static IReadOnlyList<CapabilityDescriptor> Describe(GpdControlsSnapshot snapshot)
    {
        List<CapabilityDescriptor> result =
        [
            Choice("haptic.strength", CapabilityRole.GenericChoice, "Rumble strength", ["off", "medium", "high"]),
            Range("controller.left-deadzone", "Left stick deadzone"),
            Range("controller.right-deadzone", "Right stick deadzone"),
            Range("controller.left-boundary", "Left stick boundary"),
            Range("controller.right-boundary", "Right stick boundary")
        ];
        if (snapshot.Version2 || snapshot.Firmware.Contains("K4", StringComparison.Ordinal))
        {
            result.Add(Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect, "Lighting effect",
                ["off", "solid", "breathing", "gradient"]));
            result.Add(new CapabilityDescriptor
            {
                CapabilityId = CapabilityIds.LightingColor, Role = CapabilityRole.LightingZoneColor,
                ValueKind = CapabilityValueKind.Color, Display = CapabilityDescriptors.Label("Lighting color"),
                SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            });
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private static void Apply(byte[] config, bool v2, CapabilityCommand command)
    {
        var value = command.RequestedValue!;
        switch (command.CapabilityId)
        {
            case "haptic.strength":
                var strength = (byte)(value.ChoiceValue switch
                {
                    "off" => 0, "medium" => 1, "high" => 2,
                    _ => throw new ArgumentOutOfRangeException(nameof(command))
                });
                if (v2)
                {
                    config[944] = strength;
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(config.AsSpan(66, 2), strength);
                }

                break;
            case "controller.left-deadzone":
                config[v2 ? 949 : 73] = unchecked((byte)(sbyte)value.IntegerValue!.Value); break;
            case "controller.right-deadzone":
                config[v2 ? 951 : 75] = unchecked((byte)(sbyte)value.IntegerValue!.Value); break;
            case "controller.left-boundary":
                config[v2 ? 950 : 72] = unchecked((byte)(sbyte)value.IntegerValue!.Value); break;
            case "controller.right-boundary":
                config[v2 ? 952 : 74] = unchecked((byte)(sbyte)value.IntegerValue!.Value); break;
            case CapabilityIds.LightingEffect:
                config[v2 ? 945 : 68] = (byte)(value.ChoiceValue switch
                {
                    "off" => 0, "solid" => v2 ? 0x80 : 1, "breathing" => v2 ? 0x81 : 0x11,
                    "gradient" => v2 ? 0x82 : 0x21, _ => throw new ArgumentOutOfRangeException(nameof(command))
                }); break;
            case CapabilityIds.LightingColor:
                var offset = v2 ? 946 : 69;
                var color = value.ColorValue!.Value;
                config[offset] = (byte)(color >> 16);
                config[offset + 1] = (byte)(color >> 8);
                config[offset + 2] = (byte)color;
                break;
            default: throw new ArgumentException("Unknown GPD configuration field.", nameof(command));
        }
    }


    private static CapabilityDescriptor Choice(string id, CapabilityRole role, string label, string[] choices)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = role, ValueKind = CapabilityValueKind.Choice,
            Display = CapabilityDescriptors.Label(label),
            SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent,
            Choices = CapabilityDescriptors.Choices(choices)
        };
    }

    private static CapabilityDescriptor Range(string id, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, Role = CapabilityRole.GenericRange, ValueKind = CapabilityValueKind.Integer,
            Display = CapabilityDescriptors.Label(label),
            SupportsWrite = true, Minimum = -10, Maximum = 10, Step = 1,
            Persistence = CapabilityPersistence.DevicePersistent
        };
    }
}
