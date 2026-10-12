using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;

namespace LibHandheld.Families.MsiClaw;

/// <summary>What the Claw capabilities share: HC's write-then-trust model.</summary>
/// <remarks>
///     HC writes MSI_ACPI and the MCU and never reads a value back to confirm it (its <c>WMI.Set</c>
///     ignores the result). WSGM follows that: a write that the transport accepted is applied and its
///     intent is authoritative. Controls have no readback or verification pass. A failed write is
///     reported as indeterminate without a second write, since an uncertain write is never retried.
/// </remarks>
internal static class ClawApplied
{
    public static CapabilityCommandResult Failed(CapabilityCommand command, string operation, Exception exception,
        CancellationToken cancellationToken)
    {
        DeviceTrace.Failure(operation, $"The {operation} write failed", exception);
        return CommandResults.Indeterminate(
            command,
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? CapabilityReasonCode.Quiescing
                : CapabilityReasonCode.TransportFaulted,
            $"The {operation} write failed after it began: {exception.GetType().Name}.");
    }

    public static CapabilityCommandResult Refused(CapabilityCommand command, string operation, Exception exception,
        CancellationToken cancellationToken)
    {
        return CommandResults.Rejected(command,
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? CapabilityReasonCode.Quiescing
                : CapabilityReasonCode.TransportFaulted,
            $"The {operation} preparation failed before any write: {exception.GetType().Name}.");
    }

    /// <summary>Byte 1 of a getter response: HC's <c>WMI.Get</c> strips the status byte, then reads index 0.</summary>
    /// <param name="response">Non-null getter response including its status byte.</param>
    /// <param name="index">Nonnegative response offset; ordinary scalar getters use one.</param>
    /// <param name="operation">Capability name used in truncated-response diagnostics.</param>
    /// <returns>The requested response byte.</returns>
    /// <exception cref="InvalidOperationException">The response does not contain the requested offset.</exception>
    public static byte Byte(byte[] response, int index, string operation)
    {
        return response.Length > index
            ? response[index]
            : throw new InvalidOperationException($"The {operation} getter returned a truncated response.");
    }
}

internal sealed class ClawPowerCapability(
    IMsiWmiTransport transport,
    ClawModel model,
    Func<TimeSpan, CancellationToken, Task> delay,
    TimeProvider? timeProvider = null)
{
    /// <summary>HC's <c>PerformanceManager</c> sleeps this long after each limit it writes.</summary>
    private static readonly TimeSpan WriteSpacing = TimeSpan.FromMilliseconds(200);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? throw new ArgumentNullException(nameof(delay));

    private readonly ClawModel _model = model ?? throw new ArgumentNullException(nameof(model));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private long? _lastPowerWrite;
    private byte? _scenarioEnvelope;

    internal (int Sustained, int Boost)? WrittenLimits { get; private set; }
    internal DateTimeOffset? LimitsWrittenAt { get; private set; }

    internal byte? WrittenScenario { get; private set; }
    internal DateTimeOffset? ScenarioWrittenAt { get; private set; }

    private int Minimum => _model.MinimumWatts;

    private int Maximum => _model.MaximumWatts;

    /// <summary>Captures the original temporary state once for journal restoration, never for control state.</summary>
    public async ValueTask<PowerPair> ReadAsync(CancellationToken cancellationToken)
    {
        var sustained = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.PowerSustainedAddress,
            cancellationToken).ConfigureAwait(false);
        var boost = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.PowerBoostAddress,
            cancellationToken).ConfigureAwait(false);
        var scenario = await ReadScenarioAsync(cancellationToken).ConfigureAwait(false);
        _scenarioEnvelope = scenario;
        return new PowerPair(
            ClawApplied.Byte(sustained, 1, "power"),
            ClawApplied.Byte(boost, 1, "power"),
            scenario,
            await ReadFastAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Reasserts the accepted pair on the ten-second TDP watchdog without querying control registers.
    /// </summary>
    /// <param name="cancellationToken">Cancels writes or spacing delays; an in-flight write may already have effects.</param>
    /// <returns>Completion of one watchdog attempt, or no work before an accepted explicit command.</returns>
    /// <remarks>A write failure clears the target and propagates, preventing automatic retry of uncertainty.</remarks>
    public async ValueTask ReassertAsync(CancellationToken cancellationToken)
    {
        if (WrittenLimits is not { } target || _lastPowerWrite is not { } writtenAt
                                            || _time.GetElapsedTime(writtenAt) < TimeSpan.FromSeconds(10))
        {
            return;
        }

        DeviceTrace.Change(
            "power",
            "reassert",
            $"Writing the requested {target.Sustained}/{target.Boost} W on the ten-second TDP watchdog.");
        try
        {
            _lastPowerWrite = _time.GetTimestamp();
            await WritePairAsync(target.Sustained, target.Boost, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            WrittenLimits = null;
            LimitsWrittenAt = null;
            throw;
        }
    }

    /// <summary>
    ///     Reads the SHIFT byte where HC's <c>GetShiftValue</c> does: <c>Get_AP</c> block 0, data[2],
    ///     which is response byte 3 once the status byte is counted.
    /// </summary>
    private async ValueTask<byte> ReadScenarioAsync(CancellationToken cancellationToken)
    {
        var response = await _transport.InvokeGetterAsync("Get_AP", 0, cancellationToken).ConfigureAwait(false);
        return ClawApplied.Byte(response, 3, "scenario");
    }

    /// <summary>Reads EC 0x52 where the model writes it, so a restore can put its own value back.</summary>
    /// <remarks>HC only ever writes this register. A read it refuses leaves the value unknown, never the service.</remarks>
    private async ValueTask<int?> ReadFastAsync(CancellationToken cancellationToken)
    {
        if (!_model.WritesFastLimit)
        {
            return null;
        }

        try
        {
            var fast = await _transport.InvokeGetterAsync(
                "Get_Data",
                ClawHardwareFacts.PowerFastAddress,
                cancellationToken).ConfigureAwait(false);
            return ClawApplied.Byte(fast, 1, "power");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Writes the pair a power-limit command carries, as WSGM decided it.</summary>
    /// <remarks>
    ///     Every write to PL1 or PL2 names both limits (<c>DevicePowerPair.TryResolve</c>), the way HC's
    ///     performance page hands its manager both values; the plugin derives neither from the other.
    /// </remarks>
    /// <param name="command">Validated host command carrying identity and result correlation.</param>
    /// <param name="sustainedWatts">Requested PL1 in the model's inclusive wattage range.</param>
    /// <param name="boostWatts">
    ///     Requested PL2 in the model's inclusive wattage range; the caller validates PL1 does not exceed
    ///     PL2.
    /// </param>
    /// <param name="cancellationToken">Cancels transport/delay work without proving that an accepted write was undone.</param>
    /// <returns>Range rejection, indeterminate failed write, or accepted pair without control readback.</returns>
    public async ValueTask<CapabilityCommandResult> ApplyLimitsAsync(
        CapabilityCommand command,
        int sustainedWatts,
        int boostWatts,
        CancellationToken cancellationToken)
    {
        if (sustainedWatts < Minimum || sustainedWatts > Maximum || boostWatts < Minimum || boostWatts > Maximum)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                $"PL1 and PL2 must be {Minimum}-{Maximum} W.");
        }

        return await ApplyPairCoreAsync(command, sustainedWatts, boostWatts, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CapabilityCommandResult> ApplyScenarioAsync(
        CapabilityCommand command,
        string scenario,
        CancellationToken cancellationToken)
    {
        // Preserve an original captured envelope, or use HC's SHIFT flags; a read never gates this write.
        var current = WrittenScenario ?? _scenarioEnvelope ?? 0xC0;

        int? target = scenario switch
        {
            "comfort" => ShiftTarget(current, 0),
            "green" => ShiftTarget(current, 1),
            "eco" => ShiftTarget(current, 2),
            "sport" => ShiftTarget(current, 4),
            "user" => ShiftTarget(current, _model.UserScenario),
            // ShiftModeCalcType.Deactive.
            "inactive" => ((current & 0xC3) | 0x80) & 0xBF,
            _ => null
        };
        if (target is not { } value)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, "Unknown firmware scenario.");
        }

        try
        {
            await WriteDataAsync(ClawHardwareFacts.ScenarioAddress, value, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Failed(command, "scenario", exception, cancellationToken);
        }

        WrittenScenario = (byte)value;
        ScenarioWrittenAt = _time.GetUtcNow();
        _scenarioEnvelope = (byte)value;
        return CommandResults.Applied(command);
    }

    /// <summary>Writes the captured scenario, then the captured pair, as HC applies a profile.</summary>
    /// <remarks>Complete once every write went through; the values are not read back.</remarks>
    /// <param name="snapshot">Original scenario and wattage pair, with optional independent fast limit.</param>
    /// <param name="cancellationToken">Cancels writes or inter-write delays; partial restoration may already have occurred.</param>
    /// <returns>Completion after all restore writes are accepted; transport failures propagate without readback or retry.</returns>
    public async ValueTask RestoreAsync(PowerPair snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await WriteDataAsync(ClawHardwareFacts.ScenarioAddress, snapshot.Scenario, cancellationToken)
            .ConfigureAwait(false);
        await WritePairAsync(snapshot.SustainedWatts, snapshot.BoostWatts, cancellationToken).ConfigureAwait(false);

        // The pair write set 0x52 to the boost value; the captured one goes back when it was read.
        if (_model.WritesFastLimit && snapshot.FastWatts is { } fast && fast != snapshot.BoostWatts)
        {
            await WriteDataAsync(ClawHardwareFacts.PowerFastAddress, fast, cancellationToken).ConfigureAwait(false);
        }

        WrittenLimits = null;
        LimitsWrittenAt = null;
        WrittenScenario = null;
        ScenarioWrittenAt = null;
    }

    private static int ShiftTarget(byte current, int mode)
    {
        // ShiftModeCalcType.ChangeToCurrentShiftType: (v & 0xC3) | 0xC0, then & 0xFC, plus the mode.
        return ((((current & 0xC3) | 0xC0) & 0xFC) + mode) & 0xFF;
    }

    private async ValueTask<CapabilityCommandResult> ApplyPairCoreAsync(
        CapabilityCommand command,
        int sustainedWatts,
        int boostWatts,
        CancellationToken cancellationToken)
    {
        try
        {
            await WritePairAsync(sustainedWatts, boostWatts, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            WrittenLimits = null;
            LimitsWrittenAt = null;
            return ClawApplied.Failed(command, "power", exception, cancellationToken);
        }

        WrittenLimits = (sustainedWatts, boostWatts);
        LimitsWrittenAt = _time.GetUtcNow();
        _lastPowerWrite = _time.GetTimestamp();
        return CommandResults.Applied(command);
    }

    /// <summary>
    ///     HC's order: <c>set_long_limit</c> (0x50) then <c>set_short_limit</c> (0x51, and 0x52 on the
    ///     BZ2EM straight after), with <c>PerformanceManager</c>'s 200 ms after each.
    /// </summary>
    private async ValueTask WritePairAsync(int sustainedWatts, int boostWatts, CancellationToken cancellationToken)
    {
        await WriteDataAsync(ClawHardwareFacts.PowerSustainedAddress, sustainedWatts, cancellationToken)
            .ConfigureAwait(false);
        await _delay(WriteSpacing, cancellationToken).ConfigureAwait(false);
        await WriteDataAsync(ClawHardwareFacts.PowerBoostAddress, boostWatts, cancellationToken).ConfigureAwait(false);
        if (_model.WritesFastLimit)
        {
            await WriteDataAsync(ClawHardwareFacts.PowerFastAddress, boostWatts, cancellationToken)
                .ConfigureAwait(false);
        }
    }


    private ValueTask WriteDataAsync(byte address, int value, CancellationToken cancellationToken)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = address;
        package[1] = checked((byte)value);
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }
}

internal sealed class ClawChargeLimitCapability(IMsiWmiTransport transport)
{
    internal const int MinimumPercent = 60;
    internal const int MaximumPercent = 100;

    /// <summary>HC's slider step (<c>BatteryBypassStep</c>): 60, 80 or 100.</summary>
    internal const int StepPercent = 20;

    /// <summary>MSI's Battery Master flag, bit 7 of 0xD7: the limit is only enforced while it is set.</summary>
    internal const byte BatteryMaster = 0x80;
    internal const string ProtectionId = "battery.charge-protection";

    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    internal ChargeLimitState? Written { get; private set; }
    internal DateTimeOffset? WrittenAt { get; private set; }
    internal bool? WrittenProtection { get; private set; }
    internal DateTimeOffset? ProtectionWrittenAt { get; private set; }
    private byte? _chargeEnvelope;

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        int percent,
        CancellationToken cancellationToken)
    {
        if (percent is < MinimumPercent or > MaximumPercent || percent % StepPercent != 0)
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.ValueOutOfRange,
                $"The charge limit must be {MinimumPercent}, 80 or {MaximumPercent}%.");
        }

        var wanted = Encode(percent);
        try
        {
            // HC's SetBatteryMaster(true) and SetBatteryChargeLimit together: choosing a limit enables
            // it, so bit 7 is set along with the percentage.
            await WriteRawAsync(wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Failed(command, "charge-limit", exception, cancellationToken);
        }

        Written = new ChargeLimitState(percent, wanted);
        WrittenAt = DateTimeOffset.UtcNow;
        _chargeEnvelope = wanted;
        WrittenProtection = true;
        ProtectionWrittenAt = WrittenAt;
        return CommandResults.Applied(command);
    }

    internal async ValueTask<CapabilityCommandResult> ApplyProtectionAsync(CapabilityCommand command, bool enabled,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Quiescing,
                "The Battery Master command was cancelled before dispatch.");
        }

        if (_chargeEnvelope is null)
        {
            try
            {
                var response = await _transport.InvokeGetterAsync("Get_Data", ClawHardwareFacts.ChargeLimitAddress,
                    cancellationToken).ConfigureAwait(false);
                _chargeEnvelope = ClawApplied.Byte(response, 1, "charge-protection");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Preserve a readable envelope, but never make it a prerequisite for Battery Master.
                // Without any known percentage, use the supported unrestricted 100 percent setting.
            }
        }

        var percentage = (byte)((_chargeEnvelope ?? MaximumPercent) & 0x7F);
        var wanted = (byte)(percentage | (enabled ? BatteryMaster : 0));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException exception)
        {
            return ClawApplied.Refused(command, "charge-protection", exception, cancellationToken);
        }

        try
        {
            await WriteRawAsync(wanted, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Failed(command, "charge-protection", exception, cancellationToken);
        }

        _chargeEnvelope = wanted;
        WrittenProtection = enabled;
        ProtectionWrittenAt = DateTimeOffset.UtcNow;
        if (percentage is >= MinimumPercent and <= MaximumPercent && percentage % StepPercent == 0)
        {
            Written = new ChargeLimitState(percentage, wanted);
            WrittenAt = ProtectionWrittenAt;
        }

        return CommandResults.Applied(command);
    }

    /// <summary>The register value for an enforced limit: Battery Master and the percentage.</summary>
    /// <param name="percent">Already-validated charge percentage; supported commands use 60, 80, or 100.</param>
    /// <returns>The byte value with Battery Master enabled; this helper does not validate the supported percentage choices.</returns>
    /// <exception cref="OverflowException">The input cannot be represented by a byte.</exception>
    internal static byte Encode(int percent)
    {
        return (byte)(BatteryMaster | checked((byte)percent));
    }

    private ValueTask WriteRawAsync(byte rawValue, CancellationToken cancellationToken)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = ClawHardwareFacts.ChargeLimitAddress;
        package[1] = rawValue;
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }
}

internal sealed class ClawFanCapability(IMsiWmiTransport transport)
{
    private static readonly int[] TemperatureOffsets = [1, 4, 5, 6, 7, 8];
    private static readonly int[] DutyOffsets = [2, 3, 4, 5, 6, 7];

    /// <summary>The firmware's two fan channels, always written together.</summary>
    private static readonly int[] FanChannels = [1, 2];

    private readonly IMsiWmiTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private FanSnapshot? _envelope;
    private (bool Custom, bool Full)? _writtenMode;

    internal int? WrittenDuty { get; private set; }
    internal DateTimeOffset? DutyWrittenAt { get; private set; }
    internal IReadOnlyList<CurvePoint>? WrittenCurve { get; private set; }
    internal DateTimeOffset? CurveWrittenAt { get; private set; }
    internal DateTimeOffset? ModeWrittenAt { get; private set; }

    internal string? WrittenMode => _writtenMode is { } mode
        ? mode.Custom ? "custom" : mode.Full ? "full-speed" : "automatic"
        : null;

    /// <summary>Captures original tables/flags once for restoration and byte-preserving commands.</summary>
    public async ValueTask<FanSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var left = await ReadTableAsync(1, cancellationToken).ConfigureAwait(false);
        var right = await ReadTableAsync(2, cancellationToken).ConfigureAwait(false);

        // HC's SetFanControl takes the custom-fan byte from Get_AP block 1, data[0], and writes it
        // to 0xD4; SetFanFullSpeed reads and writes 0x98 through Get_Data/Set_Data.
        var custom = await _transport.InvokeGetterAsync("Get_AP", 1, cancellationToken).ConfigureAwait(false);
        var full = await _transport.InvokeGetterAsync(
            "Get_Data",
            ClawHardwareFacts.FanFullSpeedAddress,
            cancellationToken).ConfigureAwait(false);
        _envelope = new FanSnapshot(
            left,
            right,
            ClawApplied.Byte(custom, 1, "fan mode"),
            ClawApplied.Byte(full, 1, "fan mode"));
        return _envelope;
    }

    public async ValueTask<FanTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken)
    {
        var fan = await _transport.InvokeGetterAsync("Get_Fan", 0, cancellationToken)
            .ConfigureAwait(false);
        var temperature = await _transport.InvokeGetterAsync("Get_Temperature", 0, cancellationToken)
            .ConfigureAwait(false);
        if (fan.Length < 5 || temperature.Length < 2)
        {
            throw new InvalidOperationException("The fan telemetry getter returned a truncated response.");
        }

        return new FanTelemetry(
            DecodeRpm(fan[1], fan[2]),
            DecodeRpm(fan[3], fan[4]),
            temperature[1]);
    }

    public async ValueTask<CapabilityCommandResult> ApplyDutyAsync(CapabilityCommand command, int percent,
        CancellationToken token)
    {
        if (percent is < 0 or > 100)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "Fan duty is outside 0-100 percent.");
        }

        var dispatched = false;
        try
        {
            var custom = _envelope?.CustomFlag ?? 0;
            var full = _envelope?.FullSpeedFlag ?? 0;

            token.ThrowIfCancellationRequested();
            foreach (var channel in FanChannels)
            {
                var package = new byte[ClawHardwareFacts.WmiPackageLength];
                package[0] = checked((byte)channel);
                package.AsSpan(1, 8).Fill(checked((byte)Math.Round(percent * 150d / 100)));
                dispatched = true;
                await _transport.InvokeSetterAsync("Set_Fan", package, token).ConfigureAwait(false);
            }

            await WriteRawFlagAsync(ClawHardwareFacts.FanCustomAddress, SetFlag(custom, true), token)
                .ConfigureAwait(false);
            await WriteRawFlagAsync(ClawHardwareFacts.FanFullSpeedAddress, SetFlag(full, false), token)
                .ConfigureAwait(false);
            WrittenDuty = percent;
            _writtenMode = (true, false);
            DutyWrittenAt = ModeWrittenAt = DateTimeOffset.UtcNow;
            WrittenCurve = null;
            CurveWrittenAt = null;
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return dispatched
                ? ClawApplied.Failed(command, "fan duty", exception, token)
                : ClawApplied.Refused(command, "fan duty", exception, token);
        }
    }

    public async ValueTask<CapabilityCommandResult> ApplyModeAsync(
        CapabilityCommand command,
        string mode,
        CancellationToken cancellationToken)
    {
        if (mode is not ("automatic" or "custom" or "full-speed"))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, "Unknown fan mode.");
        }

        var (custom, full) = mode switch
        {
            "automatic" => (false, false),
            "custom" => (true, false),
            _ => (false, true)
        };

        var before = _envelope;

        try
        {
            await WriteRawFlagAsync(ClawHardwareFacts.FanCustomAddress, SetFlag(before?.CustomFlag ?? 0, custom),
                cancellationToken).ConfigureAwait(false);
            await WriteRawFlagAsync(ClawHardwareFacts.FanFullSpeedAddress, SetFlag(before?.FullSpeedFlag ?? 0, full),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Failed(command, "fan mode", exception, cancellationToken);
        }

        _writtenMode = (custom, full);
        ModeWrittenAt = DateTimeOffset.UtcNow;
        return CommandResults.Applied(command);
    }

    /// <summary>Writes one curve to both fan channels.</summary>
    /// <remarks>
    ///     The two fans sit on one heatsink and the firmware ramps them together, so one curve goes to
    ///     both channels. The table layout (six points, temperatures through <c>Set_Temperature</c>) is
    ///     the one the reference unit reports through <c>Get_Fan</c>/<c>Get_Temperature</c>, and differs
    ///     from HC's eight-byte <c>SetFanTable</c>; see PROVENANCE.md.
    /// </remarks>
    /// <param name="command">Validated host command used for result identity.</param>
    /// <param name="curve">
    ///     Exactly six nondecreasing temperature/duty points, each within 0ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Å“100
    ///     Ãƒâ€šÃ‚Â°C/percent.
    /// </param>
    /// <param name="cancellationToken">Cancels reads/writes; cancellation after dispatch can leave partial effects.</param>
    /// <returns>
    ///     Validation rejection, indeterminate failed write, or accepted curve without control readback.
    /// </returns>
    public async ValueTask<CapabilityCommandResult> ApplyCurveAsync(
        CapabilityCommand command,
        IReadOnlyList<CurvePoint> curve,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCurve(curve, out var validationError))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, validationError!);
        }

        var before = _envelope;

        try
        {
            foreach (var channel in FanChannels)
            {
                var template = channel == 1 ? before?.Left : before?.Right;
                var table = Encode(template ?? new FanTable(new byte[32], new byte[32]), curve);
                await WriteTableAsync(channel, table.TemperatureBuffer, table.DutyBuffer, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Failed(command, "fan curve", exception, cancellationToken);
        }

        WrittenCurve = [.. curve];
        CurveWrittenAt = DateTimeOffset.UtcNow;
        return CommandResults.Applied(command);
    }

    /// <summary>Writes the captured tables and flags back.</summary>
    /// <remarks>Complete once every write went through; the values are not read back.</remarks>
    /// <param name="snapshot">Original left/right raw tables and mode flags, including unknown bytes to preserve.</param>
    /// <param name="cancellationToken">Cancels subsequent writes; earlier restore writes are not rolled back.</param>
    /// <returns>Completion after both tables and flags are accepted; transport failures propagate.</returns>
    public async ValueTask RestoreAsync(FanSnapshot snapshot, CancellationToken cancellationToken)
    {
        await WriteTableAsync(1, snapshot.Left.TemperatureBuffer, snapshot.Left.DutyBuffer, cancellationToken)
            .ConfigureAwait(false);
        await WriteTableAsync(2, snapshot.Right.TemperatureBuffer, snapshot.Right.DutyBuffer, cancellationToken)
            .ConfigureAwait(false);
        await WriteRawFlagAsync(ClawHardwareFacts.FanCustomAddress, snapshot.CustomFlag, cancellationToken)
            .ConfigureAwait(false);
        await WriteRawFlagAsync(ClawHardwareFacts.FanFullSpeedAddress, snapshot.FullSpeedFlag, cancellationToken)
            .ConfigureAwait(false);
        WrittenCurve = null;
        CurveWrittenAt = null;
        _writtenMode = null;
        ModeWrittenAt = null;
        WrittenDuty = null;
        DutyWrittenAt = null;
    }

    private static bool TryValidateCurve(IReadOnlyList<CurvePoint> curve, out string? error)
    {
        if (curve.Count != 6)
        {
            error = "The Claw firmware requires exactly six fan-curve points.";
            return false;
        }

        for (var i = 0; i < curve.Count; i++)
        {
            var point = curve[i];
            if (point.Input is < 0 or > 100 || point.Output is < 0 or > 100)
            {
                error = "Fan temperatures and duties must be in the validated 0-100 range.";
                return false;
            }

            if (i <= 0 || (point.Input >= curve[i - 1].Input && point.Output >= curve[i - 1].Output))
            {
                continue;
            }

            error = "Fan-curve temperatures and duties must be monotonic.";
            return false;
        }

        error = null;
        return true;
    }

    internal static IReadOnlyList<CurvePoint> DecodeCurve(FanTable table)
    {
        return
        [
            .. Enumerable.Range(0, TemperatureOffsets.Length)
                .Select(index => new CurvePoint(
                    table.TemperatureBuffer[TemperatureOffsets[index]],
                    table.DutyBuffer[DutyOffsets[index]]))
        ];
    }

    private static FanTable Encode(FanTable current, IReadOnlyList<CurvePoint> curve)
    {
        byte[] temperatures = [.. current.TemperatureBuffer];
        byte[] duties = [.. current.DutyBuffer];
        for (var i = 0; i < curve.Count; i++)
        {
            temperatures[TemperatureOffsets[i]] = checked((byte)curve[i].Input);
            duties[DutyOffsets[i]] = checked((byte)curve[i].Output);
        }

        return new FanTable(duties, temperatures);
    }


    private async ValueTask<FanTable> ReadTableAsync(byte channel, CancellationToken cancellationToken)
    {
        var duties = await _transport.InvokeGetterAsync("Get_Fan", channel, cancellationToken)
            .ConfigureAwait(false);
        var temperatures = await _transport.InvokeGetterAsync("Get_Temperature", channel, cancellationToken)
            .ConfigureAwait(false);
        return new FanTable(duties, temperatures);
    }

    private async ValueTask WriteTableAsync(
        int channel,
        byte[] temperatures,
        byte[] duties,
        CancellationToken cancellationToken)
    {
        byte[] temperaturePackage = [.. temperatures];
        byte[] dutyPackage = [.. duties];
        temperaturePackage[0] = checked((byte)channel);
        dutyPackage[0] = checked((byte)channel);
        await _transport.InvokeSetterAsync("Set_Temperature", temperaturePackage, cancellationToken)
            .ConfigureAwait(false);
        await _transport.InvokeSetterAsync("Set_Fan", dutyPackage, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask WriteRawFlagAsync(byte address, byte value, CancellationToken cancellationToken)
    {
        var package = new byte[ClawHardwareFacts.WmiPackageLength];
        package[0] = address;
        package[1] = value;
        return _transport.InvokeSetterAsync("Set_Data", package, cancellationToken);
    }

    private static int DecodeRpm(byte high, byte low)
    {
        var divisor = (high << 8) | low;
        return divisor == 0 ? 0 : 480_000 / divisor;
    }

    private static byte SetFlag(byte value, bool enabled)
    {
        return enabled ? (byte)(value | 0x80) : (byte)(value & 0x7F);
    }
}

internal sealed class ClawLightingCapability(IClawMcuTransport transport, ushort profileAddress)
{
    private static readonly TimeSpan MinimumPersistentWriteInterval = TimeSpan.FromSeconds(1);
    private readonly IClawMcuTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    private bool _envelopeCaptured;
    private LightingState? _envelopeState;
    private DateTimeOffset _lastPersistentWrite;
    private byte[]? _profile;

    /// <summary>The MCU profile address for this cycle's controller firmware.</summary>
    public ushort ProfileAddress => profileAddress;

    /// <summary>The last accepted lighting intent, never inferred from a device read.</summary>
    public LightingState? Current { get; private set; }

    internal DateTimeOffset? WrittenAt { get; private set; }

    /// <summary>Captures the original payload once for byte-preserving writes, without publishing control state.</summary>
    /// <param name="cancellationToken">Cancels the MCU read; caller cancellation propagates.</param>
    public async ValueTask CaptureEnvelopeAsync(CancellationToken cancellationToken)
    {
        _envelopeCaptured = true;
        byte[] profile;
        try
        {
            profile = await _transport.ReadProfileAsync(profileAddress, 32, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (profile.Length != 32 || profile[1] is not 1 || profile[2] is not 0x09)
        {
            return;
        }

        _profile = [.. profile];
        _envelopeState = Decode(profile);
    }

    public async ValueTask<CapabilityCommandResult> ApplyAsync(
        CapabilityCommand command,
        Func<LightingState, LightingState> update,
        CancellationToken cancellationToken)
    {
        // Capture only the original envelope once to preserve unrelated bytes/colours in a partial update.
        // This preparation cannot establish control state or prevent HC's declared write if it fails.
        if (!_envelopeCaptured)
        {
            await CaptureEnvelopeAsync(cancellationToken).ConfigureAwait(false);
        }

        // HC keeps brightness 100 and black until told otherwise.
        var before = Current ?? _envelopeState ?? new LightingState(100, 0, 0, 0);
        var wanted = update(before);
        if (wanted.Brightness is < 0 or > 100
            || !IsColor(wanted.RightRingColor)
            || !IsColor(wanted.LeftRingColor)
            || !IsColor(wanted.ButtonsColor))
        {
            return CommandResults.Rejected(
                command,
                CapabilityReasonCode.ValueOutOfRange,
                "Lighting brightness or colour is outside the validated range.");
        }

        var untilNextWrite = MinimumPersistentWriteInterval - (DateTimeOffset.UtcNow - _lastPersistentWrite);
        if (untilNextWrite > TimeSpan.Zero)
        {
            if (Deadline.After(untilNextWrite) >= command.Deadline)
            {
                return CommandResults.Rejected(
                    command,
                    new CapabilityReason(
                        CapabilityReasonCode.Quiescing,
                        "The lighting command deadline is too short for the persistent-write interval.",
                        true));
            }

            try
            {
                await Task.Delay(untilNextWrite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
            {
                return ClawApplied.Refused(command, "lighting", exception, cancellationToken);
            }
        }

        // HC's GetRGB payload, over the bytes last read where there are any so unknown bytes survive.
        var payload = Encode(wanted, _profile ?? Encode(new LightingState(0, 0, 0, 0)));
        _lastPersistentWrite = DateTimeOffset.UtcNow;
        try
        {
            await _transport.WriteProfileAsync(profileAddress, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ClawApplied.Failed(command, "lighting", exception, cancellationToken);
        }

        _profile = payload;
        Current = wanted;
        WrittenAt = DateTimeOffset.UtcNow;
        return CommandResults.Applied(command);
    }

    internal static byte[] Encode(LightingState state)
    {
        var payload = new byte[32];
        payload[0] = 0;
        payload[1] = 1;
        payload[2] = 0x09;
        payload[3] = 0x03;
        return Encode(state, payload);
    }

    private static byte[] Encode(LightingState state, byte[] template)
    {
        if (template.Length != 32)
        {
            throw new ArgumentException("A Claw lighting profile must contain exactly 32 bytes.", nameof(template));
        }

        byte[] payload = [.. template];
        payload[4] = checked((byte)state.Brightness);
        for (var zone = 0; zone < 4; zone++)
        {
            WriteColor(payload, 5 + zone * 3, state.RightRingColor);
            WriteColor(payload, 17 + zone * 3, state.LeftRingColor);
        }

        WriteColor(payload, 29, state.ButtonsColor);
        return payload;
    }

    private static LightingState Decode(byte[] profile)
    {
        return new LightingState(profile[4], ReadColor(profile, 5), ReadColor(profile, 17), ReadColor(profile, 29));
    }


    private static int ReadColor(byte[] payload, int offset)
    {
        return (payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2];
    }

    private static void WriteColor(byte[] payload, int offset, int color)
    {
        payload[offset] = checked((byte)((color >> 16) & 0xFF));
        payload[offset + 1] = checked((byte)((color >> 8) & 0xFF));
        payload[offset + 2] = checked((byte)(color & 0xFF));
    }

    private static bool IsColor(int color)
    {
        return color is >= 0 and <= 0xFFFFFF;
    }
}

internal static class CapabilityInstances
{
    public const string Left = "left";
    public const string Right = "right";
    public const string RightRing = "right-ring";
    public const string LeftRing = "left-ring";
    public const string Buttons = "buttons";
}

/// <summary>Overlay sections this plugin declares in its descriptor set.</summary>
internal static class SectionIds
{
    public const string Power = DeviceSections.PowerId;
    public const string Lighting = DeviceSections.RgbId;

    /// <summary>Read-only ownership and telemetry. Nothing here is a control.</summary>
    /// <remarks>
    ///     Was <c>input</c>, a page of three rows that only ever reported whether the plugin held the
    ///     pad, the gyro and the rumble sink. That is worth reading when something is wrong and worth
    ///     nothing the rest of the time, so it is no longer a Controller page competing with the one
    ///     that has the actual controller settings on it.
    /// </remarks>
    public const string Info = DeviceSections.InfoId;
}

/// <summary>Categories inside the declared overlay sections.</summary>
internal static class CategoryIds
{
    public const string Limits = "limits";
    public const string Charging = "charging";
    public const string Control = "control";
    public const string Readings = "readings";
    public const string Zones = "zones";
    public const string Ownership = "ownership";
}
