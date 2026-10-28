using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>Retains the controller's lighting bytes and previews changes in RAM, never in ROM.</summary>
/// <remarks>
///     Only the current animation in the retained 883-byte lighting section is edited. A selected
///     animation occupies 220 bytes: effect, nine-slot count, inverse speed, brightness, then eight
///     RGB keyframes of 27 bytes (nine slots each). Eight slots are physically mapped; slot 8 and
///     unrelated keyframes/animations/trailing bytes are preserved. Edits are left on the controller
///     when WSGM releases it; firmware returns to its stored lighting after power loss.
/// </remarks>
internal sealed class AcerLightingService(ProfiledFeatureContext context, IAcerLease<AcerControllerTransport> lane)
    : ProfiledFeatureService("acer-lighting")
{
    // Confirmed one at a time in white on controller release 0100, screen facing the user.
    // The ninth wire slot produced no visible light and is retained without exposing a zone.
    private static readonly (string Id, string Name, int Index)[] Zones =
    [
        ("left.top-left", "Left ring: top left", 6),
        ("left.top-right", "Left ring: top right", 5),
        ("left.bottom-left", "Left ring: bottom left", 7),
        ("left.bottom-right", "Left ring: bottom right", 4),
        ("right.top-left", "Right ring: top left", 1),
        ("right.top-right", "Right ring: top right", 0),
        ("right.bottom-left", "Right ring: bottom left", 2),
        ("right.bottom-right", "Right ring: bottom right", 3)
    ];
    private static readonly IReadOnlyList<CapabilityDescriptor> Controls =
    [
        ProviderControls.Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
            DisplayKey.Brightness, minimum: 0, maximum: 100, CapabilityUnit.Percent, write: true),
        ProviderControls.Integer(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed,
            DisplayKey.Custom, minimum: 0, maximum: 20, CapabilityUnit.None, write: true) with
        {
            Display = CapabilityDescriptors.Label("Lighting speed")
        },
        new()
        {
            CapabilityId = CapabilityIds.LightingEffect, Role = CapabilityRole.LightingEffect,
            Display = CapabilityDescriptors.Label("Lighting effect"), ValueKind = CapabilityValueKind.Choice,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
            Choices = CapabilityDescriptors.Choices(["solid", "breathing"])
        },
        new()
        {
            CapabilityId = CapabilityIds.LightingColor, InstanceId = "main", Role = CapabilityRole.LightingZoneColor,
            Display = CapabilityDescriptors.Label("All ring segments"), ValueKind = CapabilityValueKind.Color,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        },
        .. Zones.Select(zone => new CapabilityDescriptor
        {
            CapabilityId = CapabilityIds.LightingColor, InstanceId = zone.Id, Role = CapabilityRole.LightingZoneColor,
            Display = CapabilityDescriptors.Label(zone.Name), ValueKind = CapabilityValueKind.Color,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        })
    ];
    private readonly ObservedValues _observed = new();
    private AcerControllerTransport? _controller;
    private byte[]? _lighting;
    private string _binding = context.Firmware;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => Controls;
    internal override IReadOnlyList<CapabilityState> States => Controls.Select(item =>
        ProviderControls.State(item.CapabilityId, State == DeviceServiceState.Owned,
            _observed.Value(item.CapabilityId, item.InstanceId), _observed.Time(item.CapabilityId, item.InstanceId),
            Reason) with { InstanceId = item.InstanceId }).ToArray();
    public override bool Suspendable => true;

    /// <summary>Opens the exact measured command lane, retires any old lighting original, and retains a complete fresh section.</summary>
    /// <remarks>Unlike a simple scalar readback, retaining unrelated profile bytes is necessary to construct a safe edit.</remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _controller = lane.Acquire();
        if (_controller is null)
        {
            return Set(DeviceServiceState.Passive, Missing("The measured Atlas controller lighting lane is unavailable."));
        }

        _binding = $"{context.Firmware}|controller:{_controller.Release:X4}";
        // Lighting is the user's choice and stays on the controller after WSGM stops; restoring the
        // OEM rainbow on every exit undid it. Earlier builds journaled an original, so drop it unrestored.
        await context.Journal.RetireAsync(ServiceId, "lighting edits are kept after release", token)
            .ConfigureAwait(false);
        _lighting = await _controller.ReadLightingAsync(token).ConfigureAwait(false);
        Observe(_lighting, written: false);
        return Set(DeviceServiceState.Owned);
    }

    /// <summary>Validates the selected animation and returns its offset inside the lighting section.</summary>
    /// <remarks>
    ///     Byte 0 selects one of four animations. Its header starts at 1 + index*220: effect 1..8,
    ///     slot count exactly 9, inverse speed 0..20 and brightness 0..100. Unknown but well-shaped
    ///     effects are retained; only solid 1 and breathing 2 have authored runtime controls.
    /// </remarks>
    internal static int AnimationOffset(byte[] lighting)
    {
        if (lighting.Length != AcerControllerTransport.LightingLength || lighting[0] > 3)
        {
            throw new IOException("The Atlas lighting section has an unsupported shape.");
        }

        var offset = 1 + lighting[0] * 220;
        if (lighting[offset] is < 1 or > 8 || lighting[offset + 1] != 9
            || lighting[offset + 2] > 20 || lighting[offset + 3] > 100)
        {
            throw new IOException("The Atlas animation fields are outside the documented format.");
        }

        return offset;
    }

    /// <summary>Projects the current animation's header and first keyframe into shared and spatial controls.</summary>
    /// <remarks>
    ///     UI speed is 20 minus the wire byte. Colors are RGB triplets; All ring segments is known
    ///     only when the eight visible slots agree, regardless of the preserved ninth slot.
    /// </remarks>
    private void Observe(byte[] lighting, bool written)
    {
        var offset = AnimationOffset(lighting);
        Store(CapabilityIds.LightingBrightness, CapabilityValue.Integer(lighting[offset + 3]), written);
        Store(CapabilityIds.LightingSpeed, CapabilityValue.Integer(20 - lighting[offset + 2]), written);
        var effect = lighting[offset] switch { 1 => "solid", 2 => "breathing", _ => null };
        Store(CapabilityIds.LightingEffect, effect is null ? null : CapabilityValue.Choice(effect), written);
        var colors = lighting.AsSpan(offset + 4, 24);
        var uniform = true;
        for (var index = 3; index < colors.Length; index += 3)
        {
            uniform &= colors[..3].SequenceEqual(colors.Slice(index, 3));
        }

        Store(CapabilityIds.LightingColor, uniform ? CapabilityValue.Color((colors[0] << 16) | (colors[1] << 8) | colors[2]) : null,
            written, instance: "main");
        foreach (var zone in Zones)
        {
            var rgb = colors.Slice(zone.Index * 3, 3);
            Store(CapabilityIds.LightingColor, CapabilityValue.Color((rgb[0] << 16) | (rgb[1] << 8) | rgb[2]),
                written, instance: zone.Id);
        }
    }

    private void Store(string id, CapabilityValue? value, bool written, string? instance = null)
    {
        if (written && value is not null)
        {
            _observed.Written(id, value, instance);
        }
        else
        {
            _observed.Read(id, value, instance);
        }
    }

    /// <summary>Clones retained bytes and changes only the requested supported header field or visible RGB slots.</summary>
    /// <remarks>
    ///     Solid/breathing use effect 1/2. Breathing clears only the eight visible colors in its second
    ///     keyframe to reproduce the vendor's fade-to-black sequence. A color edit on another effect
    ///     selects solid; all other animation data remains retained. This pure encoder performs no I/O
    ///     and does not generate a replacement profile or write the unassigned ninth RGB slot.
    /// </remarks>
    internal static byte[] Edit(byte[] retained, CapabilityCommand command)
    {
        var offset = AnimationOffset(retained);
        var lighting = (byte[])retained.Clone();
        switch (command.CapabilityId)
        {
            case CapabilityIds.LightingBrightness:
                lighting[offset + 3] = checked((byte)command.RequestedValue!.IntegerValue!.Value);
                break;
            case CapabilityIds.LightingSpeed:
                lighting[offset + 2] = checked((byte)(20 - command.RequestedValue!.IntegerValue!.Value));
                break;
            case CapabilityIds.LightingEffect:
                lighting[offset] = command.RequestedValue!.ChoiceValue switch
                {
                    "solid" => 1, "breathing" => 2,
                    _ => throw new ArgumentException("Unsupported Atlas lighting effect.")
                };
                // A second black frame is the vendor's breathing sequence. Other animations are retained.
                if (lighting[offset] == 2)
                {
                    lighting.AsSpan(offset + 4 + 27, 24).Clear();
                }
                break;
            case CapabilityIds.LightingColor:
                var color = command.RequestedValue!.ColorValue!.Value;
                var shared = command.InstanceId is null or "main";
                var zoneIndex = Array.FindIndex(Zones, zone => zone.Id == command.InstanceId);
                if (!shared && zoneIndex < 0)
                {
                    throw new ArgumentException("Unknown Atlas lighting zone.");
                }
                if (lighting[offset] > 2)
                {
                    lighting[offset] = 1;
                }
                foreach (var zone in shared ? Zones : [Zones[zoneIndex]])
                {
                    lighting[offset + 4 + zone.Index * 3] = (byte)((color >> 16) & 0xFF);
                    lighting[offset + 5 + zone.Index * 3] = (byte)((color >> 8) & 0xFF);
                    lighting[offset + 6 + zone.Index * 3] = (byte)(color & 0xFF);
                }
                break;
            default:
                throw new ArgumentException("Unknown Atlas lighting control.");
        }

        _ = AnimationOffset(lighting);
        return lighting;
    }

    /// <summary>Journals the complete original section, writes the changed RAM range once, and publishes edited values.</summary>
    /// <remarks>
    ///     A post-dispatch failure may have applied some chunks. It invalidates the entire cached
    ///     section and observation set; only a subsequent explicit command obtains fresh bytes before
    ///     another edit. No automatic resend, ROM save or arbitrary firmware animation synthesis occurs.
    /// </remarks>
    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_controller is null || State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The Atlas controller lighting lane is unavailable.");
        }

        var dispatched = false;
        try
        {
            // A fresh read after an uncertain command resolves its bytes before any explicit next action.
            _lighting ??= await _controller.ReadLightingAsync(token).ConfigureAwait(false);
            var changed = Edit(_lighting, command);
            await _controller.WriteLightingAsync(changed, token, () => dispatched = true, _lighting).ConfigureAwait(false);
            _lighting = changed;
            Observe(changed, written: true);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _lighting = null;
                _observed.Clear();
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Closes the command handle and leaves the user's lighting on the controller.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        await DisposeAsync().ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    /// <summary>Releases the handle and cached section only; recovery writes belong to ReleaseAsync.</summary>
    internal override async ValueTask DisposeAsync()
    {
        if (_controller is not null)
        {
            _controller = null;
            await lane.ReleaseAsync().ConfigureAwait(false);
        }

        _lighting = null;
    }
}
