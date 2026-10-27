using System.Buffers.Binary;
using System.Globalization;
using System.Management;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.Acer;

/// <summary>Firmware-discovered fan groups using Acer's WMI commands, without direct EC register access.</summary>
/// <remarks>
///     Controls use root\WMI AcerGamingFunction; discovery, measured RPM and CPU temperature use the
///     separate EC HID health command 0008. Thus fan duty is a firmware 0..100 command, not measured
///     RPM or a calibrated PWM percentage. Auto and Max are distinct behaviors, not duty 0/100 aliases.
///     Each discovered fan retains its own original behavior/speed before mutation. Unreadable originals
///     never block control, but recovery falls back to firmware Auto and reports ReleasedUnverified.
/// </remarks>
internal sealed class AcerFanService(ProfiledFeatureContext context) : ProfiledFeatureService("acer-fans")
{
    private readonly ObservedValues _observed = new();
    private IReadOnlyList<AcerFan> _fans = [];
    private IReadOnlyList<CapabilityDescriptor> _descriptors = [];
    private ManagementObject? _wmi;
    private AcerEcTransport? _ec;
    private bool _inexactRestore;
    private bool _topologyDecided;
    private bool _cpuTemperature;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;
    internal override IReadOnlyList<CapabilityState> States => _descriptors.Select(item =>
        ProviderControls.State(item.CapabilityId, State == DeviceServiceState.Owned,
            _observed.Value(item.CapabilityId, item.InstanceId), _observed.Time(item.CapabilityId, item.InstanceId),
            Reason) with { InstanceId = item.InstanceId }).ToArray();
    public override bool Suspendable => true;

    /// <summary>Opens WMI/EC, decides topology once, reconciles each fan's original and samples observations.</summary>
    /// <remarks>Resume reuses the initially selected topology; telemetry/readback failures do not redefine controls.</remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _wmi = WmiProvider.TryOpen("root\\WMI", "SELECT * FROM AcerGamingFunction");
        _ec = AcerEcTransport.Open();
        if (_wmi is null || _ec is null)
        {
            return Set(DeviceServiceState.Passive, Missing("Acer fan discovery or its WMI provider is unavailable."));
        }

        if (!_topologyDecided)
        {
            var reply = _ec.Exchange([0, 0xA0, 8, 0, 1, 0], token);
            var health = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(5));
            _fans = Discover(health);
            _cpuTemperature = (health & 1) != 0;
            _topologyDecided = true;
        }
        _descriptors = _fans.SelectMany<AcerFan, CapabilityDescriptor>(fan =>
        [
            new CapabilityDescriptor
            {
                CapabilityId = CapabilityIds.FanMode, InstanceId = fan.Id, Role = CapabilityRole.FanMode,
                Display = CapabilityDescriptors.Label($"{fan.Name} fan mode"), ValueKind = CapabilityValueKind.Choice,
                SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
                Choices = CapabilityDescriptors.Choices(["automatic", "full-speed", "custom"])
            },
            ProviderControls.Integer("fan.duty", CapabilityRole.FanDuty, DisplayKey.FanSpeed,
                minimum: 0, maximum: 100, CapabilityUnit.Percent, write: true) with
            {
                InstanceId = fan.Id, Display = CapabilityDescriptors.Label($"{fan.Name} fan speed")
            },
            .. fan.RpmIndex is null ? Array.Empty<CapabilityDescriptor>() : new[]
            {
                ProviderControls.Integer(CapabilityIds.FanRpm, CapabilityRole.FanMeasuredRpm, DisplayKey.FanSpeed,
                    minimum: 0, maximum: 65535, CapabilityUnit.Rpm, write: false) with
                {
                    InstanceId = fan.Id, Display = CapabilityDescriptors.Label($"{fan.Name} fan RPM")
                }
            }
        ]).ToArray();
        if (_cpuTemperature)
        {
            _descriptors =
            [
                .. _descriptors,
                ProviderControls.Integer(CapabilityIds.Temperature, CapabilityRole.Telemetry, DisplayKey.CpuTemperature,
                    minimum: 0, maximum: 150, CapabilityUnit.Celsius, write: false)
            ];
        }
        foreach (var fan in _fans)
        {
            await context.Journal.ReconcileAsync<AcerFanOriginal>(RecoveryId(fan), context.Firmware,
                (original, ct) => RestoreAsync(fan, original, ct), token).ConfigureAwait(false);
        }

        Observe();
        ObserveRpm(token);
        return Set(_fans.Count == 0 ? DeviceServiceState.Passive : DeviceServiceState.Owned,
            _fans.Count == 0 ? Missing("The firmware did not report a supported fan group.") : null);
    }

    internal override ValueTask RefreshTelemetryAsync(CancellationToken token)
    {
        if (State == DeviceServiceState.Owned)
        {
            ObserveRpm(token);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reads health operation 2: selected item at offset 5, uint16 measurement at offset 7.</summary>
    /// <remarks>SYS/SYS2 indices 4/8 are RPM; health index 1 is CPU degrees Celsius, accepted only up to 150.</remarks>
    private void ObserveRpm(CancellationToken token)
    {
        foreach (var fan in _fans.Where(fan => fan.RpmIndex is not null))
        {
            try
            {
                var reply = _ec!.Exchange([0, 0xA0, 8, 0, 2, fan.RpmIndex!.Value], token);
                _observed.Read(CapabilityIds.FanRpm,
                    CapabilityValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(7))), fan.Id);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
            {
                _observed.Read(CapabilityIds.FanRpm, null, fan.Id);
            }
        }

        if (_cpuTemperature)
        {
            try
            {
                var reply = _ec!.Exchange([0, 0xA0, 8, 0, 2, 1], token);
                var temperature = BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(7));
                _observed.Read(CapabilityIds.Temperature,
                    temperature <= 150 ? CapabilityValue.Integer(temperature) : null);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
            {
                _observed.Read(CapabilityIds.Temperature, null);
            }
        }
    }

    /// <summary>Translates the health-support mask to fan command selectors without assuming a fixed fan count.</summary>
    /// <param name="healthMask">Command 0008/operation 1 reply uint16 at payload offset 5.</param>
    /// <returns>CPU from bit 1; System count from bits 3/7; GPU count from bits 5/8.</returns>
    /// <remarks>
    ///     Only the physically tested 02CD mask labels SYS/SYS2 Left/Right. Other supported masks keep
    ///     generic group names; a desktop UI's two rows are not evidence of two physical fans.
    /// </remarks>
    internal static IReadOnlyList<AcerFan> Discover(ushort healthMask)
    {
        List<AcerFan> fans = [];
        if ((healthMask & (1 << 1)) != 0)
        {
            fans.Add(new AcerFan("cpu", "CPU", SelectionBit: 0, BehaviorShift: 0, SpeedSelector: 1));
        }

        var systems = ((healthMask >> 3) & 1) + ((healthMask >> 7) & 1);
        var gpus = ((healthMask >> 5) & 1) + ((healthMask >> 8) & 1);
        for (var index = 0; index < systems; index++)
        {
            var name = healthMask == 0x02CD
                ? index == 0 ? "Left" : "Right"
                : index == 0 ? "System" : "System 2";
            fans.Add(new AcerFan(index == 0 ? "sys" : "sys2", name,
                index + 1, 2 + 2 * index, index + 2, index == 0 ? (byte)4 : (byte)8));
        }

        for (var index = 0; index < gpus; index++)
        {
            fans.Add(new AcerFan(index == 0 ? "gpu" : "gpu2", index == 0 ? "GPU" : "GPU 2",
                index + 3, 6 + 2 * index, index + 4));
        }

        return fans;
    }

    /// <summary>Invokes a fan getter with uint32 gmInput and retains the complete uint64 gmOutput.</summary>
    private ulong Read(string method, uint input)
    {
        using var arguments = _wmi!.GetMethodParameters(method);
        arguments["gmInput"] = input;
        using var reply = WmiProvider.Invoke(_wmi, method, arguments);
        return reply?["gmOutput"] is { } value
            ? Convert.ToUInt64(value, CultureInfo.InvariantCulture)
            : throw new IOException($"Acer {method} returned no value.");
    }

    /// <summary>Invokes a packed uint64 setter, accepts a zero low status byte and applies 30 ms vendor pacing.</summary>
    /// <remarks>Cancellation is checked before invocation; missing/nonzero status after dispatch is effect unknown.</remarks>
    private void Write(string method, ulong input, CancellationToken token, Action? dispatched = null)
    {
        using var arguments = _wmi!.GetMethodParameters(method);
        arguments["gmInput"] = input;
        token.ThrowIfCancellationRequested();
        dispatched?.Invoke();
        using var reply = WmiProvider.Invoke(_wmi, method, arguments);
        var status = reply?["gmOutput"] is { } value
            ? Convert.ToUInt32(value, CultureInfo.InvariantCulture)
            : throw new IOException($"Acer {method} returned no status; effect unknown.");
        if ((status & 0xFF) != 0)
        {
            throw new IOException($"Acer {method} returned 0x{status:X8}; effect unknown.");
        }

        Thread.Sleep(millisecondsTimeout: 30);
    }

    /// <summary>Decodes getter layouts into recoverable raw behavior and optional manual speed.</summary>
    /// <remarks>
    ///     Both getters reserve the low byte for status. Behavior is at bit 8 plus the fan's two-bit
    ///     shift, whereas setter behavior starts at bit 16. Zero is unknown, not Auto; only 1/2/3 are
    ///     valid. Speed is bits 8..15 and is retained only for Manual behavior 3 and values at most 100.
    /// </remarks>
    private AcerFanOriginal Capture(AcerFan fan)
    {
        int? behavior = null;
        int? speed = null;
        try
        {
            var reply = Read("GetGamingFanBehavior", checked((uint)(1 << fan.SelectionBit)));
            var value = checked((int)((reply >> (8 + fan.BehaviorShift)) & 3));
            if ((reply & 0xFF) == 0 && value is >= 1 and <= 3)
            {
                behavior = value;
            }

            reply = Read("GetGamingFanSpeed", checked((uint)fan.SpeedSelector));
            value = checked((int)((reply >> 8) & 0xFF));
            if ((reply & 0xFF) == 0 && value <= 100 && behavior == 3)
            {
                speed = value;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"{fan.Name} original unavailable: {exception.Message}");
        }

        return new AcerFanOriginal { Behavior = behavior, Speed = speed };
    }

    private void Observe()
    {
        foreach (var fan in _fans)
        {
            var original = Capture(fan);
            _observed.Read(CapabilityIds.FanMode,
                original.Behavior is { } behavior ? CapabilityValue.Choice(ModeName(behavior)) : null, fan.Id);
            _observed.Read("fan.duty", original.Speed is { } speed ? CapabilityValue.Integer(speed) : null, fan.Id);
        }
    }

    /// <summary>Journals one fan, writes its behavior, then writes duty only when a duty command requests it.</summary>
    /// <remarks>
    ///     Modes map automatic/full-speed/custom to firmware 1/2/3. A duty write first selects behavior
    ///     3. The writes are not a firmware transaction: failure after either dispatch discards both
    ///     cached values and reports partial/uncertain effects without repeating either write.
    /// </remarks>
    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        var fan = _fans.FirstOrDefault(item => item.Id == command.InstanceId);
        if (_wmi is null || fan is null || State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "The requested Acer fan is unavailable.");
        }

        var dispatched = false;
        try
        {
            var behavior = command.CapabilityId == "fan.duty" ? 3 : command.RequestedValue?.ChoiceValue switch
            {
                "automatic" => 1, "full-speed" => 2, "custom" => 3,
                _ => throw new ArgumentException("Unknown Acer fan mode.")
            };
            if (command.CapabilityId == "fan.duty" && command.RequestedValue?.IntegerValue is not (>= 0 and <= 100))
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    "Fan speed must be between 0 and 100.");
            }

            var recovery = await context.Journal.BeginAsync(RecoveryId(fan), context.Firmware, () => Capture(fan), token)
                .ConfigureAwait(false);
            if (recovery?.Entry.OriginalState.Behavior is null)
            {
                _inexactRestore = true;
            }

            Write("SetGamingFanBehavior", fan.PackBehavior(behavior), token, () => dispatched = true);
            _observed.Written(CapabilityIds.FanMode, CapabilityValue.Choice(ModeName(behavior)), fan.Id);
            if (command.CapabilityId == "fan.duty")
            {
                var speed = command.RequestedValue!.IntegerValue!.Value;
                Write("SetGamingFanSpeed", fan.PackSpeed(speed), token, () => dispatched = true);
                _observed.Written("fan.duty", CapabilityValue.Integer(speed), fan.Id);
            }

            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Forget(CapabilityIds.FanMode, fan.Id);
                _observed.Forget("fan.duty", fan.Id);
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Restores known behavior/manual duty, or safely returns cooling to firmware Auto when either is unknown.</summary>
    /// <remarks>The Auto fallback is deliberately unverified; a successful setter cannot manufacture the missing original.</remarks>
    private async ValueTask RestoreAsync(AcerFan fan, AcerFanOriginal original, CancellationToken token)
    {
        // Unknown prior behavior returns cooling to firmware control. It is never an exact restore.
        var behavior = original.Behavior is >= 1 and <= 3 ? original.Behavior.Value : 1;
        if (original.Behavior is not (>= 1 and <= 3) || behavior == 3 && original.Speed is not (>= 0 and <= 100))
        {
            _inexactRestore = true;
            behavior = 1;
        }

        Write("SetGamingFanBehavior", fan.PackBehavior(behavior), token);
        if (behavior == 3 && original.Speed is { } speed)
        {
            Write("SetGamingFanSpeed", fan.PackSpeed(speed), token);
        }

        var restored = Capture(fan);
        var verified = original.Behavior == restored.Behavior
                       && (behavior != 3 || original.Speed == restored.Speed)
                       && original.Behavior is >= 1 and <= 3;
        _inexactRestore |= !verified;

        await context.Journal.SetStatusAsync(RecoveryId(fan),
            verified ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoredUnverified,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Reconciles every fan before disposing both native providers; incomplete originals remain recoverable.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            foreach (var fan in _fans)
            {
                await context.Journal.ReconcileAsync<AcerFanOriginal>(RecoveryId(fan), context.Firmware,
                    (original, ct) => RestoreAsync(fan, original, ct), token).ConfigureAwait(false);
            }

            return Set(_inexactRestore || _fans.Any(fan => context.Journal.HasUnrestoredMutation(RecoveryId(fan)))
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Releases WMI and HID resources only; does not independently reset fan behavior.</summary>
    internal override ValueTask DisposeAsync()
    {
        _wmi?.Dispose();
        _wmi = null;
        _ec?.Dispose();
        _ec = null;
        return ValueTask.CompletedTask;
    }

    private static string RecoveryId(AcerFan fan)
    {
        return $"acer.fan.{fan.Id}";
    }

    private static string ModeName(int behavior)
    {
        return behavior switch { 1 => "automatic", 2 => "full-speed", 3 => "custom", _ => "unknown" };
    }
}

/// <summary>A discovered fan's independent behavior-mask, packed-mode and fixed-speed addressing.</summary>
/// <param name="Id">Stable capability/recovery instance, such as sys or sys2.</param>
/// <param name="Name">Physical name only where tested, otherwise the firmware group label.</param>
/// <param name="SelectionBit">Bit in the setter/getter selection mask.</param>
/// <param name="BehaviorShift">The fan's two-bit position, added to setter bit 16 or getter bit 8.</param>
/// <param name="SpeedSelector">One-based selector in the low byte of fixed-speed requests.</param>
/// <param name="RpmIndex">Optional EC health measurement index; absence does not remove fan control.</param>
internal sealed record AcerFan(string Id, string Name, int SelectionBit, int BehaviorShift, int SpeedSelector,
    byte? RpmIndex = null)
{
    /// <summary>Packs a raw 1/2/3 behavior into its setter lane while selecting only this fan.</summary>
    internal ulong PackBehavior(int behavior)
    {
        return (checked((ulong)behavior) << (16 + BehaviorShift)) | (1UL << SelectionBit);
    }

    /// <summary>Packs the 0..100 speed command above the low-byte fan selector.</summary>
    internal ulong PackSpeed(int speed)
    {
        return ((ulong)checked((uint)speed) << 8) | checked((uint)SpeedSelector);
    }
}

/// <summary>Raw recoverable fan state; null values mean unreadable originals and require the Auto fallback.</summary>
internal sealed record AcerFanOriginal
{
    public int? Behavior { get; init; }
    public int? Speed { get; init; }
}
