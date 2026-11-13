using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>The Atlas's separate ENE rear-logo LED; controller ring profiles never address it.</summary>
/// <remarks>
///     A1 lists targets, A2 selects one for the A3 capability query, and A4 sets the selected target's
///     effect, brightness, speed, RGB and LED mask. There is no current-lighting getter. Values start
///     unknown and are published only after an accepted complete write. The Atlas 8 establishes the initial
///     Off effect, full brightness and speed-5 values once at start. Nothing is sent on stop. The Atlas 7
///     also turns a logo commanded this session off for sleep.
/// </remarks>
internal sealed class AcerLogoLightingService(bool darkenForSleep = false, bool initializeUnknownValues = false,
    Func<IAcerLogoLightingTransport?>? open = null)
    : ProfiledFeatureService("acer-logo-lighting")
{
    private const string Instance = "logo";
    private readonly ObservedValues _observed = new();
    private bool _initializationAttempted;
    private IReadOnlyList<CapabilityDescriptor> _controls = [];
    private IAcerLogoLightingTransport? _transport;
    private bool _commanded;
    private bool _darkened;
    private string _effect = "solid";
    private int _brightness = 100;
    private int _speed = 5;
    private int _color = 0xFFFFFF;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _controls;
    internal override IReadOnlyList<CapabilityState> States => _controls.Select(control =>
        ProviderControls.Observed(control, _observed, State == DeviceServiceState.Owned, Reason)).ToArray();
    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!initializeUnknownValues || !_initializationAttempted)
        {
            _observed.Clear();
        }
        try
        {
            _transport = (open ?? AcerLogoLightingTransport.TryOpen)();
            if (_transport is null)
            {
                _controls = [];
                return Set(DeviceServiceState.Passive, Missing("The Atlas rear-logo lighting interface is unavailable."));
            }
            var report = new byte[11];
            report[0] = 0xA1;
            _transport.Read(report);
            if (report[0] != 0xA1 || report[1] is < 1 or > 9 || !report.AsSpan(2, report[1]).Contains((byte)0x80))
            {
                throw new IOException("The ENE lighting interface does not list the rear logo.");
            }
            Array.Clear(report);
            report[0] = 0xA2;
            report[1] = 0x80;
            _transport.Write(report);
            Array.Clear(report);
            report[0] = 0xA3;
            _transport.Read(report);
            if (report[0] != 0xA3 || report[1] != 0x80 || report[2] != 1 || report[4] != 1)
            {
                throw new IOException("The rear-logo capability response is unsupported.");
            }
            var modes = BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(5));
            var effects = new[] { ("off", 1), ("solid", 2), ("breathing", 4) }
                .Where(effect => (modes & (1U << (effect.Item2 - 1))) != 0).Select(effect => effect.Item1).ToArray();
            if (!effects.Contains("solid") || !effects.Contains("off"))
            {
                throw new IOException("The rear logo does not declare steady color and off support.");
            }
            _controls =
            [
                CapabilityDescriptors.Integer(CapabilityIds.LightingBrightness, CapabilityRole.LightingBrightness,
                    CapabilityDescriptors.Label("Rear logo brightness"), minimum: 0, maximum: 100,
                    CapabilityUnit.Percent, read: false, write: true, instance: Instance),
                CapabilityDescriptors.Choice(CapabilityIds.LightingEffect, CapabilityRole.LightingEffect,
                    CapabilityDescriptors.Label("Rear logo effect"), CapabilityDescriptors.Choices(effects),
                    read: false, write: true, instance: Instance),
                CapabilityDescriptors.Integer(CapabilityIds.LightingSpeed, CapabilityRole.LightingEffectSpeed,
                    CapabilityDescriptors.Label("Rear logo speed"), minimum: 0, maximum: 10,
                    CapabilityUnit.None, read: false, write: true, instance: Instance),
                CapabilityDescriptors.Color(CapabilityIds.LightingColor, Instance,
                    CapabilityDescriptors.Label("Rear logo color"), read: false, CapabilityPersistence.Volatile)
            ];
            RelightAfterSleep();
            var result = Set(DeviceServiceState.Owned);
            if (initializeUnknownValues && !_initializationAttempted)
            {
                _initializationAttempted = true;
                _effect = "off";
                var command = new CapabilityCommand
                {
                    CommandId = Guid.NewGuid(), CapabilityId = CapabilityIds.LightingSpeed, InstanceId = Instance,
                    RequestedValue = CapabilityValue.Integer(_speed), Deadline = cycle.Deadline
                };
                var applied = await ExecuteAsync(command, string.Empty, token).ConfigureAwait(false);
                DeviceTrace.Write(applied.Outcome == CommandOutcome.Applied ? DeviceTraceLevel.Info : DeviceTraceLevel.Warn,
                    ServiceId, $"Initial rear logo: {applied.Outcome}; requested Off, brightness 100, speed 5. {applied.Reason?.Detail}");
            }
            return result;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            _transport?.Dispose();
            _transport = null;
            _controls = [];
            return Set(DeviceServiceState.Passive, Missing(exception.Message));
        }
    }

    /// <summary>Relights a logo blacked out for sleep with the values last commanded.</summary>
    private void RelightAfterSleep()
    {
        if (!_darkened)
        {
            return;
        }

        _darkened = false;
        var report = Packet(_effect, _brightness, _speed, _color);
        try
        {
            _transport!.Write(report);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _observed.Clear();
            DeviceTrace.Warn(ServiceId, $"The rear logo could not be relit after sleep: {exception.Message}");
        }
    }

    /// <summary>Blacks out the Atlas 7's logo before the cycle stops; the Atlas 8 path is a plain release.</summary>
    /// <remarks>A4 has no getter, so only a logo commanded this session has values to bring back.</remarks>
    public override ValueTask<DeviceServiceResult> SuspendAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        if (darkenForSleep && _commanded && _transport is not null && State == DeviceServiceState.Owned)
        {
            var report = Packet("off", 0, 0, 0);
            try
            {
                _transport.Write(report);
                _darkened = true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _darkened = false;
                _observed.Clear();
                DeviceTrace.Warn(ServiceId, $"The rear logo could not be blacked out for sleep: {exception.Message}");
            }
        }

        return ReleaseAsync(cycle, token);
    }

    /// <summary>Encodes the 11-byte A4 report. The final word is a one-LED mask, not a duration.</summary>
    internal static byte[] Packet(string effect, int brightness, int speed, int color)
    {
        if (brightness is < 0 or > 100 || speed is < 0 or > 10 || color is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(brightness));
        }
        var mode = effect switch
        {
            "off" => (byte)1, "solid" => (byte)2, "breathing" => (byte)4,
            _ => throw new ArgumentException("Unsupported rear-logo effect.", nameof(effect))
        };
        return [0xA4, 0x80, mode, (byte)brightness, (byte)speed, 0,
            (byte)(color >> 16), (byte)(color >> 8), (byte)color, 1, 0];
    }

    internal override ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (_transport is null || State != DeviceServiceState.Owned)
        {
            return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The rear-logo lighting interface is unavailable."));
        }
        var dispatched = false;
        try
        {
            var effect = _effect;
            var brightness = _brightness;
            var speed = _speed;
            var color = _color;
            switch (command.CapabilityId)
            {
                case CapabilityIds.LightingBrightness:
                    brightness = command.RequestedValue!.IntegerValue!.Value;
                    break;
                case CapabilityIds.LightingEffect:
                    effect = command.RequestedValue!.ChoiceValue!;
                    break;
                case CapabilityIds.LightingSpeed:
                    speed = command.RequestedValue!.IntegerValue!.Value;
                    break;
                case CapabilityIds.LightingColor:
                    color = command.RequestedValue!.ColorValue!.Value;
                    break;
                default:
                    throw new ArgumentException("Unknown rear-logo control.");
            }
            var report = Packet(effect, brightness, speed, color);
            token.ThrowIfCancellationRequested();
            dispatched = true;
            _transport.Write(report);
            _effect = effect;
            _brightness = brightness;
            _speed = speed;
            _color = color;
            _commanded = true;
            _observed.Written(CapabilityIds.LightingEffect, CapabilityValue.Choice(effect), Instance);
            _observed.Written(CapabilityIds.LightingBrightness, CapabilityValue.Integer(brightness), Instance);
            _observed.Written(CapabilityIds.LightingSpeed, CapabilityValue.Integer(speed), Instance);
            _observed.Written(CapabilityIds.LightingColor, CapabilityValue.Color(color), Instance);
            return ValueTask.FromResult(CommandResults.Applied(command));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Clear();
            }
            return ValueTask.FromResult(CommandResults.TransportFailure(command, dispatched, exception));
        }
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        await DisposeAsync().ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    internal override ValueTask DisposeAsync()
    {
        _transport?.Dispose();
        _transport = null;
        return ValueTask.CompletedTask;
    }
}
