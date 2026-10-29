using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;

namespace LibHandheld.Families.Generic;

/// <summary>Actual attached Aura modules and their persistent preference writes.</summary>
internal sealed class AsusAuraController(
    Func<IReadOnlyList<AsusAuraModule>>? discover = null,
    Func<AsusAuraModule, IAsusAuraTransport>? open = null)
{
    private readonly Dictionary<string, Entry> _modules = new(StringComparer.Ordinal);
    internal bool DescriptorsChanged { get; private set; }

    internal bool Any => _modules.Count != 0;

    internal bool Available => _modules.Values.Any(entry =>
        entry.Transport is not null);

    internal IReadOnlyList<CapabilityDescriptor> Descriptors => Array.AsReadOnly(_modules.Values
        .OrderBy(entry => entry.Module.ZoneId, StringComparer.Ordinal)
        .SelectMany(entry => Describe(entry.Module)).ToArray());

    internal IReadOnlyList<CapabilityState> States
    {
        get
        {
            var states = new List<CapabilityState>();
            foreach (var entry in _modules.Values)
            foreach (var descriptor in Describe(entry.Module))
            {
                var observed = entry.Values.GetValueOrDefault((descriptor.CapabilityId, descriptor.InstanceId));
                states.Add(new CapabilityState
                {
                    CapabilityId = descriptor.CapabilityId, InstanceId = descriptor.InstanceId,
                    Available = entry.Transport is not null,
                    ObservedValue = observed.Value, ObservedAt = observed.Timestamp,
                    Quality = observed.Value is null ? HardwareStateQuality.Unknown : HardwareStateQuality.Observed,
                    Reason = null
                });
            }

            return Array.AsReadOnly(states.ToArray());
        }
    }

    internal void AcknowledgeDescriptors()
    {
        DescriptorsChanged = false;
    }

    internal void Refresh(CancellationToken token, IReadOnlyList<AsusAuraModule>? modules = null)
    {
        token.ThrowIfCancellationRequested();
        var found = modules ?? (discover ?? AsusAuraTransport.Discover)();
        var keys = found.Select(module => module.ZoneId).ToHashSet(StringComparer.Ordinal);
        var changed = false;
        foreach (var missing in _modules.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            _modules[missing].Transport?.Dispose();
            _modules.Remove(missing);
            changed = true;
        }

        foreach (var module in found)
        {
            token.ThrowIfCancellationRequested();
            if (_modules.TryGetValue(module.ZoneId, out var entry))
            {
                if (entry.Module.HardwareIdentity != module.HardwareIdentity)
                {
                    entry.Transport?.Dispose();
                    entry = new Entry(module);
                    _modules[module.ZoneId] = entry;
                    changed = true;
                }
                else if (!string.Equals(entry.Module.Collection.DevicePath, module.Collection.DevicePath,
                             StringComparison.OrdinalIgnoreCase))
                {
                    entry.Transport?.Dispose();
                    entry.Transport = null;
                    entry.Initialized = false;
                    entry.Module = module;
                    changed = true;
                }
            }
            else
            {
                entry = new Entry(module);
                _modules.Add(module.ZoneId, entry);
                changed = true;
            }

            if (entry.Transport is null)
            {
                try
                {
                    entry.Transport = (open ?? (value => new AsusAuraTransport(value)))(module);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    /* Discovery is retried; no command was submitted. */
                }
            }
        }

        if (changed)
        {
            DescriptorsChanged = true;
        }
    }

    internal bool Handles(CapabilityCommand command)
    {
        return _modules.Values.Any(entry => Describe(entry.Module)
            .Any(descriptor =>
                descriptor.CapabilityId == command.CapabilityId && descriptor.InstanceId == command.InstanceId));
    }

    internal CapabilityCommandResult Execute(CapabilityCommand command, CancellationToken token)
    {
        var entry = _modules.Values.SingleOrDefault(candidate => Describe(candidate.Module)
            .Any(descriptor =>
                descriptor.CapabilityId == command.CapabilityId && descriptor.InstanceId == command.InstanceId));
        if (entry?.Transport is null)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The Aura module is absent.");
        }

        var dispatched = false;
        var preferences = (entry.Boot, entry.Awake, entry.Sleep, entry.Shutdown, entry.Effect,
            entry.Primary, entry.Secondary, entry.Speed, entry.Left);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!entry.Initialized)
            {
                Send(entry, AsusAuraProtocol.Handshake(entry.Module.ProductId), token, () => dispatched = true);
                foreach (var packet in AsusAuraProtocol.Initialize(entry.Module.ProductId))
                {
                    Send(entry, packet, token, () => dispatched = true);
                }

                entry.Initialized = true;
            }

            var value = command.RequestedValue!;
            var product = entry.Module.ProductId;
            switch (command.CapabilityId)
            {
                case CapabilityIds.LightingBrightness:
                    Send(entry, AsusAuraProtocol.Brightness(product, value.IntegerValue!.Value), token,
                        () => dispatched = true);
                    break;
                case "asus.aura.boot-lighting":
                    entry.Boot = value.BooleanValue!.Value;
                    Power();
                    break;
                case "asus.aura.sleep-lighting":
                    entry.Sleep = value.BooleanValue!.Value;
                    Power();
                    break;
                case "asus.aura.shutdown-lighting":
                    entry.Shutdown = value.BooleanValue!.Value;
                    Power();
                    break;
                case "asus.aura.power":
                    entry.Awake = value.BooleanValue!.Value;
                    Power();
                    break;
                case CapabilityIds.LightingEffect:
                    entry.Effect = value.ChoiceValue!;
                    Color();
                    break;
                case CapabilityIds.LightingSpeed:
                    entry.Speed = value.IntegerValue!.Value;
                    Color();
                    break;
                case "asus.aura.direction":
                    entry.Left = value.ChoiceValue == "left";
                    Color();
                    break;
                case CapabilityIds.LightingColor:
                    if (command.InstanceId!.EndsWith("-secondary", StringComparison.Ordinal))
                    {
                        entry.Secondary = value.ColorValue!.Value;
                    }
                    else
                    {
                        entry.Primary = value.ColorValue!.Value;
                    }

                    Color();
                    break;
                default:
                    return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                        "Unknown Aura module control.");
            }

            var observedValue = command.CapabilityId switch
            {
                CapabilityIds.LightingBrightness => CapabilityValue.Integer((int)Math.Round(Math.Clamp(
                    (int)Math.Round(value.IntegerValue!.Value * 3d / 100), 0, 3) * 100d / 3)),
                CapabilityIds.LightingSpeed => CapabilityValue.Integer(EncodedSpeed(entry)),
                CapabilityIds.LightingColor => CapabilityValue.Color(
                    command.InstanceId!.EndsWith("-secondary", StringComparison.Ordinal)
                        ? entry.Effect == "duality" ? entry.Secondary : 0
                        : entry.Effect == "cycle"
                            ? 0
                            : entry.Primary),
                _ => value
            };
            entry.Values[(command.CapabilityId, command.InstanceId)] = (observedValue, DateTimeOffset.UtcNow);
            return CommandResults.Applied(command);

            void Power()
            {
                Send(entry, AsusAuraProtocol.Power(product, entry.Boot, entry.Awake, entry.Sleep, entry.Shutdown),
                    token, () => dispatched = true);
                Observe("asus.aura.power", entry.Module.ZoneId, CapabilityValue.Boolean(entry.Awake));
                if (product != AsusAuraProtocol.Xg2025)
                {
                    Observe("asus.aura.boot-lighting", entry.Module.ZoneId, CapabilityValue.Boolean(entry.Boot));
                    Observe("asus.aura.sleep-lighting", entry.Module.ZoneId, CapabilityValue.Boolean(entry.Sleep));
                    Observe("asus.aura.shutdown-lighting", entry.Module.ZoneId,
                        CapabilityValue.Boolean(entry.Shutdown));
                }
            }

            void Color()
            {
                Send(entry,
                    AsusAuraProtocol.Color(product, entry.Effect, entry.Primary, entry.Secondary, entry.Speed,
                        entry.Left), token, () => dispatched = true);
                foreach (var packet in AsusAuraProtocol.Apply(product))
                {
                    Send(entry, packet, token, () => dispatched = true);
                }

                Observe(CapabilityIds.LightingEffect, entry.Module.ZoneId, CapabilityValue.Choice(entry.Effect));
                Observe(CapabilityIds.LightingColor, entry.Module.ZoneId,
                    CapabilityValue.Color(entry.Effect == "cycle" ? 0 : entry.Primary));
                Observe(CapabilityIds.LightingColor, entry.Module.ZoneId + "-secondary",
                    CapabilityValue.Color(entry.Effect == "duality" ? entry.Secondary : 0));
                Observe(CapabilityIds.LightingSpeed, entry.Module.ZoneId, CapabilityValue.Integer(EncodedSpeed(entry)));
            }

            void Observe(string id, string instance, CapabilityValue observed)
            {
                entry.Values[(id, instance)] = (observed, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            (entry.Boot, entry.Awake, entry.Sleep, entry.Shutdown, entry.Effect,
                entry.Primary, entry.Secondary, entry.Speed, entry.Left) = preferences;
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    private static void Send(Entry entry, byte[] packet, CancellationToken token, Action dispatched)
    {
        token.ThrowIfCancellationRequested();
        dispatched();
        entry.Transport!.Send(packet);
    }

    internal void Close()
    {
        foreach (var entry in _modules.Values)
        {
            entry.Transport?.Dispose();
        }

        if (_modules.Count != 0)
        {
            DescriptorsChanged = true;
        }

        _modules.Clear();
    }

    private static IReadOnlyList<CapabilityDescriptor> Describe(AsusAuraModule module)
    {
        var instance = module.ZoneId;
        var label = module.Label;
        List<CapabilityDescriptor> descriptors =
        [
            Toggle("asus.aura.power", CapabilityRole.LightingPower, instance, label + " lighting"),
            new()
            {
                CapabilityId = CapabilityIds.LightingBrightness, InstanceId = instance,
                Role = CapabilityRole.LightingBrightness,
                ValueKind = CapabilityValueKind.Integer, Minimum = 0, Maximum = 100, Step = 1,
                Unit = CapabilityUnit.Percent,
                Display = CapabilityDescriptors.Label(label + " brightness"), SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            },
            new()
            {
                CapabilityId = CapabilityIds.LightingEffect, InstanceId = instance,
                Role = CapabilityRole.LightingEffect,
                ValueKind = CapabilityValueKind.Choice, Display = CapabilityDescriptors.Label(label + " effect"),
                SupportsWrite = true,
                Choices = CapabilityDescriptors.Choices(AsusAuraProtocol.Effects(module.ProductId)),
                Persistence = CapabilityPersistence.DevicePersistent
            },
            new()
            {
                CapabilityId = CapabilityIds.LightingSpeed, InstanceId = instance,
                Role = CapabilityRole.LightingEffectSpeed,
                ValueKind = CapabilityValueKind.Integer, Minimum = 0, Maximum = 100, Step = 1,
                Unit = CapabilityUnit.Percent,
                Display = CapabilityDescriptors.Label(label + " effect speed"), SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            },
            Color(instance, label + " color"), Color(instance + "-secondary", label + " second color")
        ];
        if (module.ProductId != AsusAuraProtocol.Xg2025)
        {
            descriptors.Add(Toggle("asus.aura.boot-lighting", CapabilityRole.GenericToggle, instance,
                label + " lighting at boot"));
            descriptors.Add(Toggle("asus.aura.sleep-lighting", CapabilityRole.GenericToggle, instance,
                label + " lighting while asleep"));
            descriptors.Add(Toggle("asus.aura.shutdown-lighting", CapabilityRole.GenericToggle, instance,
                label + " lighting after shutdown"));
        }

        if (module.ProductId == AsusAuraProtocol.Lightbar)
        {
            descriptors.Add(new CapabilityDescriptor
            {
                CapabilityId = "asus.aura.direction", InstanceId = instance,
                Role = CapabilityRole.GenericChoice, ValueKind = CapabilityValueKind.Choice,
                Display = CapabilityDescriptors.Label(label + " cycle direction"),
                Choices =
                [
                    new CapabilityChoice("left", CapabilityDescriptors.Label("Left")),
                    new CapabilityChoice("right", CapabilityDescriptors.Label("Right"))
                ],
                SupportsWrite = true,
                Persistence = CapabilityPersistence.DevicePersistent
            });
        }

        return descriptors;
    }


    private static int EncodedSpeed(Entry entry)
    {
        return entry.Effect == "solid" ? 0 : entry.Speed <= 33 ? 0 : entry.Speed <= 66 ? 50 : 100;
    }

    private static CapabilityDescriptor Toggle(string id, CapabilityRole role, string instance, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = id, InstanceId = instance, Role = role, ValueKind = CapabilityValueKind.Boolean,
            Display = CapabilityDescriptors.Label(label),
            SupportsWrite = true, Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private static CapabilityDescriptor Color(string instance, string label)
    {
        return new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor, InstanceId = instance, Role = CapabilityRole.LightingZoneColor,
            ValueKind = CapabilityValueKind.Color,
            Display = CapabilityDescriptors.Label(label), SupportsWrite = true,
            Persistence = CapabilityPersistence.DevicePersistent
        };
    }

    private sealed class Entry(AsusAuraModule module)
    {
        internal bool Awake = true;

        // HHD's explicit power preference defaults; they are not hardware observations.
        internal bool Boot = true;
        internal string Effect = "solid";
        internal bool Initialized;
        internal bool Left = true;
        internal AsusAuraModule Module = module;
        internal int Primary;
        internal int Secondary;
        internal bool Shutdown;
        internal bool Sleep;
        internal int Speed = 50;
        internal IAsusAuraTransport? Transport;

        internal Dictionary<(string, string?), (CapabilityValue? Value, DateTimeOffset? Timestamp)> Values { get; } =
            [];
    }
}
