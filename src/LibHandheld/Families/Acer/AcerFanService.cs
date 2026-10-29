using System.Buffers.Binary;
using System.Collections.Concurrent;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>Firmware-discovered fan groups using Acer's WMI commands, without direct EC register access.</summary>
/// <remarks>
///     Controls use root\WMI AcerGamingFunction; discovery, measured RPM and CPU temperature use the
///     separate EC HID health command 0008. Duty is an absolute share of full speed, about 70 RPM per
///     percent on the measured PA08-I51. The firmware applies a speed command absolutely only for a few
///     seconds after a behavior write and then adds it to its own automatic level, so while WSGM owns a
///     fan in manual the hold loop re-arms behavior 3 every second; the 2026-10-29 calibration in
///     PROVENANCE.md shows that keeps both fans at the commanded speed. Auto and Max are distinct
///     behaviors, not duty aliases. Each discovered fan retains its own original behavior/speed before
///     mutation. Unreadable originals never block control, but recovery falls back to firmware Auto and
///     reports ReleasedUnverified.
/// </remarks>
internal sealed class AcerFanService(ProfiledFeatureContext context, IAcerLease<IAcerEcTransport> ec,
    Func<IAcerFanWmi?>? openWmi = null, Func<CancellationToken, ValueTask>? holdTick = null)
    : ProfiledFeatureService("acer-fans")
{
    /// <summary>Re-arm cadence; the firmware adds its automatic level about five seconds after a behavior write.</summary>
    internal static readonly TimeSpan HoldInterval = TimeSpan.FromSeconds(1);

    private readonly ObservedValues _observed = new();
    private readonly Lock _wmiGate = new();
    private readonly HashSet<string> _held = [];
    private readonly ConcurrentQueue<string> _lostHolds = new();
    private IReadOnlyList<AcerFan> _fans = [];
    private IReadOnlyList<CapabilityDescriptor> _descriptors = [];
    private IAcerFanWmi? _wmi;
    private IAcerEcTransport? _ec;
    private CancellationTokenSource? _holdCancellation;
    private Task _hold = Task.CompletedTask;
    private bool _holdRunning;
    private bool _inexactRestore;
    private bool _topologyDecided;
    private bool _cpuTemperature;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;
    internal override IReadOnlyList<CapabilityState> States => _descriptors.Select(item =>
        ProviderControls.State(item.CapabilityId, State == DeviceServiceState.Owned,
            _observed.Value(item.CapabilityId, item.InstanceId), _observed.Time(item.CapabilityId, item.InstanceId),
            Reason) with { InstanceId = item.InstanceId }).ToArray();
    public override bool Suspendable => true;

    /// <summary>The running manual re-arm loop, completed while no fan is held.</summary>
    internal Task HoldCompletion => _hold;

    /// <summary>Opens WMI/EC, decides topology once, reconciles each fan's original and samples observations.</summary>
    /// <remarks>Resume reuses the initially selected topology; telemetry/readback failures do not redefine controls.</remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _lostHolds.Clear();
        _wmi = (openWmi ?? AcerFanWmi.TryOpen)();
        _ec = ec.Acquire();
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
                minimum: AcerFan.MinimumDuty, maximum: 100, CapabilityUnit.Percent, write: true) with
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
            DrainLostHolds();
            ObserveRpm(token);
        }

        return ValueTask.CompletedTask;
    }

    // Fan RPM and the CPU temperature are the only live state; maintenance must sample them too.
    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        return RefreshTelemetryAsync(token);
    }

    /// <summary>Reads health operation 2: selected item at offset 5, uint16 measurement at offset 7.</summary>
    /// <remarks>SYS/SYS2 indices 4/8 are RPM; health index 1 is CPU degrees Celsius, accepted only up to 150.</remarks>
    private void ObserveRpm(CancellationToken token)
    {
        Span<byte> reply = stackalloc byte[9];
        for (var index = 0; index < _fans.Count; index++)
        {
            var fan = _fans[index];
            if (fan.RpmIndex is not { } rpmIndex)
            {
                continue;
            }

            try
            {
                _ec!.Exchange([0, 0xA0, 8, 0, 2, rpmIndex], reply, token);
                _observed.Read(CapabilityIds.FanRpm,
                    CapabilityValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(reply[7..])), fan.Id);
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
                _ec!.Exchange([0, 0xA0, 8, 0, 2, 1], reply, token);
                var temperature = BinaryPrimitives.ReadUInt16LittleEndian(reply[7..]);
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

    /// <summary>Invokes a fan getter under the WMI gate shared with the hold loop.</summary>
    private ulong Read(string method, uint input)
    {
        lock (_wmiGate)
        {
            return (_wmi ?? throw new IOException("Acer fan WMI is closed.")).Read(method, input);
        }
    }

    /// <summary>Invokes a packed uint64 setter, accepts a zero low status byte and applies 30 ms vendor pacing.</summary>
    /// <remarks>Cancellation is checked before invocation; missing/nonzero status after dispatch is effect unknown.</remarks>
    private void Write(string method, ulong input, CancellationToken token, Action? dispatched = null)
    {
        lock (_wmiGate)
        {
            var wmi = _wmi ?? throw new IOException("Acer fan WMI is closed.");
            token.ThrowIfCancellationRequested();
            dispatched?.Invoke();
            var status = wmi.Write(method, input);
            if ((status & 0xFF) != 0)
            {
                throw new IOException($"Acer {method} returned 0x{status:X8}; effect unknown.");
            }

            Thread.Sleep(millisecondsTimeout: 30);
        }
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
    ///     3 and the fan then joins the hold loop. The writes are not a firmware transaction: failure
    ///     after either dispatch discards both cached values, drops the hold and reports partial/uncertain
    ///     effects without repeating either write.
    /// </remarks>
    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        DrainLostHolds();
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
            if (command.CapabilityId == "fan.duty"
                && command.RequestedValue?.IntegerValue is not (>= AcerFan.MinimumDuty and <= 100))
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                    $"Fan speed must be between {AcerFan.MinimumDuty} and 100.");
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

            await HoldAsync(fan, behavior == 3).ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Forget(CapabilityIds.FanMode, fan.Id);
                _observed.Forget("fan.duty", fan.Id);
                await HoldAsync(fan, false).ConfigureAwait(false);
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Adds or removes a fan from the re-arm set, starting the loop or joining it when it empties.</summary>
    private async ValueTask HoldAsync(AcerFan fan, bool held)
    {
        bool any;
        var start = false;
        lock (_held)
        {
            if (held)
            {
                _held.Add(fan.Id);
            }
            else
            {
                _held.Remove(fan.Id);
            }

            any = _held.Count > 0;
            if (any && !_holdRunning)
            {
                // Decided under the same lock the loop uses to leave, so a fan is never held without a loop.
                _holdRunning = true;
                start = true;
            }
        }

        if (!any)
        {
            await StopHoldAsync().ConfigureAwait(false);
        }
        else if (start)
        {
            await _hold.ConfigureAwait(false);
            _holdCancellation?.Dispose();
            _holdCancellation = new CancellationTokenSource();
            var token = _holdCancellation.Token;
            _hold = Task.Run(() => RunHoldAsync(token));
        }
    }

    private async ValueTask StopHoldAsync()
    {
        lock (_held)
        {
            _held.Clear();
            _holdRunning = false;
        }

        _holdCancellation?.Cancel();
        await _hold.ConfigureAwait(false);
        _holdCancellation?.Dispose();
        _holdCancellation = null;
    }

    /// <summary>Re-arms behavior 3 for every held fan each interval so the firmware keeps the absolute speed.</summary>
    /// <remarks>
    ///     A failed re-arm is resolved by readback rather than repeated blindly: a fan the firmware still
    ///     reports manual stays held, any other answer drops the hold and forgets that fan's values on
    ///     the next owner pass. Observations are never touched from this thread.
    /// </remarks>
    private async Task RunHoldAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(HoldInterval);
        try
        {
            while (true)
            {
                if (holdTick is { } tick)
                {
                    await tick(token).ConfigureAwait(false);
                }
                else if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    return;
                }

                foreach (var fan in _fans)
                {
                    bool held;
                    lock (_held)
                    {
                        held = _held.Contains(fan.Id);
                    }

                    if (!held)
                    {
                        continue;
                    }

                    try
                    {
                        Write("SetGamingFanBehavior", fan.PackBehavior(behavior: 3), token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        if (Capture(fan).Behavior == 3)
                        {
                            continue;
                        }

                        lock (_held)
                        {
                            _held.Remove(fan.Id);
                        }

                        _lostHolds.Enqueue(fan.Id);
                        DeviceTrace.Warn(ServiceId, $"{fan.Name} manual hold lost: {exception.Message}");
                    }
                }

                lock (_held)
                {
                    if (_held.Count == 0)
                    {
                        _holdRunning = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void DrainLostHolds()
    {
        while (_lostHolds.TryDequeue(out var id))
        {
            _observed.Forget(CapabilityIds.FanMode, id);
            _observed.Forget("fan.duty", id);
        }
    }

    /// <summary>Restores known behavior/manual duty, or safely returns cooling to firmware Auto when either is unknown.</summary>
    /// <remarks>The Auto fallback is deliberately unverified; a successful setter cannot manufacture the missing original.</remarks>
    private async ValueTask RestoreAsync(AcerFan fan, AcerFanOriginal original, CancellationToken token)
    {
        // Unknown prior behavior returns cooling to firmware control. It is never an exact restore.
        var exact = original.Behavior is 1 or 2 || original.Behavior == 3 && original.Speed is >= 0 and <= 100;
        var behavior = exact ? original.Behavior!.Value : 1;
        _inexactRestore |= !exact;

        // A restored manual original is the firmware's own meaning of manual, so it is not held.
        await HoldAsync(fan, false).ConfigureAwait(false);
        Write("SetGamingFanBehavior", fan.PackBehavior(behavior), token);
        if (behavior == 3 && original.Speed is { } speed)
        {
            Write("SetGamingFanSpeed", fan.PackSpeed(speed), token);
        }

        var restored = Capture(fan);
        var verified = original.Behavior == restored.Behavior
                       && (behavior != 3 || original.Speed == restored.Speed)
                       && exact;
        _inexactRestore |= !verified;

        await context.Journal.SetStatusAsync(RecoveryId(fan),
            verified ? DeviceRecoveryStatus.RestoredVerified : DeviceRecoveryStatus.RestoredUnverified,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Stops the hold loop, reconciles every fan, then disposes both native providers.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            await StopHoldAsync().ConfigureAwait(false);
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
    internal override async ValueTask DisposeAsync()
    {
        await StopHoldAsync().ConfigureAwait(false);
        lock (_wmiGate)
        {
            _wmi?.Dispose();
            _wmi = null;
        }

        if (_ec is not null)
        {
            _ec = null;
            await ec.ReleaseAsync().ConfigureAwait(false);
        }
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
    /// <summary>The lowest commandable duty: 0 and 5 stalled and restarted the measured fans, 10 (about 700 RPM) held.</summary>
    internal const int MinimumDuty = 10;

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
