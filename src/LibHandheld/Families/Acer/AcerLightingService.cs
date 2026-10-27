using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>Retains the controller's lighting bytes and previews changes in RAM, never in ROM.</summary>
internal sealed class AcerLightingService(ProfiledFeatureContext context) : ProfiledFeatureService("acer-lighting")
{
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
            CapabilityId = "lighting.color", InstanceId = "main", Role = CapabilityRole.LightingZoneColor,
            Display = CapabilityDescriptors.Label("Lighting color"), ValueKind = CapabilityValueKind.Color,
            SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile
        }
    ];
    private readonly ObservedValues _observed = new();
    private AcerControllerTransport? _controller;
    private byte[]? _lighting;
    private string _binding = context.Firmware;
    private bool _restoreUnverified;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => Controls;
    internal override IReadOnlyList<CapabilityState> States => Controls.Select(item =>
        ProviderControls.State(item.CapabilityId, State == DeviceServiceState.Owned,
            _observed.Value(item.CapabilityId, item.InstanceId), _observed.Time(item.CapabilityId, item.InstanceId),
            Reason) with { InstanceId = item.InstanceId }).ToArray();
    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _controller = AcerControllerTransport.Open();
        if (_controller is null)
        {
            return Set(DeviceServiceState.Passive, Missing("The measured Atlas controller lighting lane is unavailable."));
        }

        _binding = $"{context.Firmware}|controller:{_controller.Release:X4}";
        await context.Journal.ReconcileAsync<AcerLightingOriginal>(ServiceId, _binding, RestoreAsync, token)
            .ConfigureAwait(false);
        _lighting = await _controller.ReadLightingAsync(token).ConfigureAwait(false);
        Observe(_lighting, written: false);
        return Set(DeviceServiceState.Owned);
    }

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

    private void Observe(byte[] lighting, bool written)
    {
        var offset = AnimationOffset(lighting);
        Store(CapabilityIds.LightingBrightness, CapabilityValue.Integer(lighting[offset + 3]), written);
        Store(CapabilityIds.LightingSpeed, CapabilityValue.Integer(20 - lighting[offset + 2]), written);
        var effect = lighting[offset] switch { 1 => "solid", 2 => "breathing", _ => null };
        Store(CapabilityIds.LightingEffect, effect is null ? null : CapabilityValue.Choice(effect), written);
        var colors = lighting.AsSpan(offset + 4, 27);
        var uniform = true;
        for (var index = 3; index < colors.Length; index += 3)
        {
            uniform &= colors[..3].SequenceEqual(colors.Slice(index, 3));
        }

        Store("lighting.color", uniform ? CapabilityValue.Color((colors[0] << 16) | (colors[1] << 8) | colors[2]) : null,
            written, instance: "main");
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
                    lighting.AsSpan(offset + 4 + 27, 27).Clear();
                }
                break;
            case "lighting.color":
                var color = command.RequestedValue!.ColorValue!.Value;
                if (lighting[offset] > 2)
                {
                    lighting[offset] = 1;
                }
                for (var index = 0; index < 9; index++)
                {
                    lighting[offset + 4 + index * 3] = (byte)((color >> 16) & 0xFF);
                    lighting[offset + 5 + index * 3] = (byte)((color >> 8) & 0xFF);
                    lighting[offset + 6 + index * 3] = (byte)(color & 0xFF);
                }
                break;
            default:
                throw new ArgumentException("Unknown Atlas lighting control.");
        }

        _ = AnimationOffset(lighting);
        return lighting;
    }

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
            await context.Journal.BeginAsync(ServiceId, _binding,
                new AcerLightingOriginal { Lighting = (byte[])_lighting.Clone() }, token).ConfigureAwait(false);
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

    private async ValueTask RestoreAsync(AcerLightingOriginal original, CancellationToken token)
    {
        _ = AnimationOffset(original.Lighting);
        // Use known current bytes to keep ordinary restoration to the changed range. After an
        // uncertain write the retained complete lighting section remains the recovery authority.
        await _controller!.WriteLightingAsync(original.Lighting, token, previous: _lighting).ConfigureAwait(false);
        bool? verified = null;
        try
        {
            verified = (await _controller.ReadLightingAsync(token).ConfigureAwait(false)).SequenceEqual(original.Lighting);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"Restored lighting observation unavailable: {exception.Message}");
        }

        _restoreUnverified |= verified != true;
        await context.Journal.SetStatusAsync(ServiceId,
            verified == true ? DeviceRecoveryStatus.RestoredVerified
                : verified == false ? DeviceRecoveryStatus.RestoreFailed : DeviceRecoveryStatus.RestoredUnverified,
            CancellationToken.None).ConfigureAwait(false);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            if (_controller is not null)
            {
                await context.Journal.ReconcileAsync<AcerLightingOriginal>(ServiceId, _binding, RestoreAsync, token)
                    .ConfigureAwait(false);
            }

            return Set(_restoreUnverified || context.Journal.HasUnrestoredMutation(ServiceId)
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    internal override async ValueTask DisposeAsync()
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
            _controller = null;
        }

        _lighting = null;
    }
}

internal sealed record AcerLightingOriginal
{
    public byte[] Lighting { get; init; } = [];
}
