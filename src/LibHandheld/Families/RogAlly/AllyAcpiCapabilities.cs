using System.ComponentModel;
using LibHandheld.Contracts;
using LibHandheld.Families.Asus;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>The three package power limits and the performance mode, as last read.</summary>
/// <param name="Sustained">SPL in watts, or null when the firmware does not report it.</param>
/// <param name="Slow">SPPT in watts, or null.</param>
/// <param name="Fast">FPPT in watts, or null.</param>
/// <param name="Mode">Performance mode (0 performance, 1 turbo, 2 silent), or null.</param>
internal sealed record AllyPowerState(int? Sustained, int? Slow, int? Fast, int? Mode)
{
    public bool LimitsReadable => Sustained is not null && Slow is not null && Fast is not null;
}

/// <summary>The fan curves captured together, one per channel the firmware exposes.</summary>
/// <param name="Cpu">CPU curve as eight temperatures followed by eight duties, or null when unreadable.</param>
/// <param name="Gpu">GPU curve in the same format, or null when unreadable.</param>
/// <param name="Mid">Mid-fan curve, or null when the channel is unavailable or unreadable.</param>
/// <param name="Mode">Performance mode used to select the queried curves, or null when unknown.</param>
internal sealed record AllyFanSnapshot(byte[]? Cpu, byte[]? Gpu, byte[]? Mid, int? Mode)
{
    public bool Readable => Cpu is not null && Gpu is not null;
}

/// <summary>Power limits and performance mode over ATKACPI.</summary>
/// <remarks>
///     HC writes SPL on its long limit and SPPT plus FPPT together on its short limit
///     (<c>AsusACPI.cs:343-352</c>); HHD writes FPPT, then SPPT, then SPL, all to the same value when
///     boost is off (<c>adjustor/drivers/asus/__init__.py:384-389</c>). This keeps HC's two-limit model,
///     so the boost descriptor drives SPPT and FPPT as one value. Writes go SPL, then SPPT and FPPT,
///     as HC writes them.
/// </remarks>
/// <param name="acpi">Shared serialized ATKACPI transport; this capability does not own its lifetime.</param>
/// <param name="model">Exact model supplying admitted watt limits.</param>
/// <param name="delay">Cancellable spacing between writes and after a mode change.</param>
internal sealed class AllyPowerCapability(
    IAsusAcpi acpi,
    AllyModel model,
    Func<TimeSpan, CancellationToken, Task> delay)
{
    /// <summary>HHD's <c>TDP_DELAY</c> between limit writes.</summary>
    internal static readonly TimeSpan WriteSpacing = TimeSpan.FromMilliseconds(100);

    /// <summary>The settle time before writing limits after a performance-mode change.</summary>
    private static readonly TimeSpan ModeSettle = TimeSpan.FromMilliseconds(150);

    private readonly IAsusAcpi _acpi = acpi ?? throw new ArgumentNullException(nameof(acpi));

    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? throw new ArgumentNullException(nameof(delay));

    private readonly AllyModel _model = model ?? throw new ArgumentNullException(nameof(model));


    public int Minimum => _model.MinimumWatts;

    public int Maximum => _model.MaximumWatts;

    /// <summary>What the firmware reports. A limit it reports as 0 W, as the Xbox Ally X does, is unknown.</summary>
    /// <returns>Independently queried limits and mode, with null for unavailable or invalid values; no write is performed.</returns>
    public AllyPowerState Read()
    {
        return new AllyPowerState(
            Watts(AsusAcpiId.SustainedPower),
            Watts(AsusAcpiId.SlowPower),
            Watts(AsusAcpiId.FastPower),
            Scalar(AsusAcpiId.PerformanceMode) is { } mode && AsusAcpiProtocol.ScenarioName(mode) is not null
                ? mode
                : null);
    }

    /// <summary>Writes the pair a power-limit command carries, as WSGM decided it.</summary>
    /// <remarks>
    ///     Every write to SPL or the boost pair names both (<c>DevicePowerPair.TryResolve</c>); the plugin
    ///     derives neither from the other. The boost value goes to SPPT and FPPT together, HC's short limit.
    /// </remarks>
    /// <param name="command">Admitted command whose identity is retained in the result.</param>
    /// <param name="sustained">SPL watts, within this model's limits.</param>
    /// <param name="boost">SPPT and FPPT watts, within this model's limits.</param>
    /// <param name="cancellationToken">Cancels inter-write delays; earlier native writes can already have occurred.</param>
    /// <returns>
    ///     Rejected for an invalid range, unverified after all writes return, or indeterminate after failure; no rollback
    ///     occurs.
    /// </returns>
    public async ValueTask<CapabilityCommandResult> ApplyLimitsAsync(
        CapabilityCommand command,
        int sustained,
        int boost,
        CancellationToken cancellationToken)
    {
        if (!InRange(sustained) || !InRange(boost))
        {
            return OutOfRange(command);
        }

        try
        {
            await WriteLimitsAsync(sustained, boost, boost, cancellationToken).ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AllyApplied.Failed(command, "power limits", ex, cancellationToken);
        }
    }

    public async ValueTask<CapabilityCommandResult> ApplyScenarioAsync(
        CapabilityCommand command,
        string scenario,
        CancellationToken cancellationToken)
    {
        byte target;
        try
        {
            target = AsusAcpiProtocol.ScenarioValue(scenario);
        }
        catch (ArgumentOutOfRangeException)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, "Unknown performance mode.");
        }

        try
        {
            _ = _acpi.Write(AsusAcpiId.PerformanceMode, target);
            // A mode change resets the limits; the next limit command publishes its own pair.
            await _delay(ModeSettle, cancellationToken).ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AllyApplied.Failed(command, "performance mode", ex, cancellationToken);
        }
    }

    /// <summary>Restores the captured mode, then SPL, SPPT and FPPT, without any readback.</summary>
    /// <param name="original">Complete captured mode and all three limits.</param>
    /// <param name="cancellationToken">Cancels the settling and spacing delays; completed writes are not undone.</param>
    /// <returns>Completion after all native writes return; incomplete snapshots, cancellation and native failures throw.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="original" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The snapshot lacks any limit or performance mode.</exception>
    public async ValueTask RestoreAsync(AllyPowerState original, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!original.LimitsReadable || original.Mode is not { } mode)
        {
            throw new InvalidOperationException("The power restore snapshot is incomplete.");
        }

        _ = _acpi.Write(AsusAcpiId.PerformanceMode, checked((uint)mode));
        await _delay(ModeSettle, cancellationToken).ConfigureAwait(false);
        await WriteLimitsAsync(original.Sustained!.Value, original.Slow!.Value, original.Fast!.Value,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask WriteLimitsAsync(int sustained, int slow, int fast, CancellationToken cancellationToken)
    {
        _ = _acpi.Write(AsusAcpiId.SustainedPower, checked((uint)sustained));
        await _delay(WriteSpacing, cancellationToken).ConfigureAwait(false);
        _ = _acpi.Write(AsusAcpiId.SlowPower, checked((uint)slow));
        await _delay(WriteSpacing, cancellationToken).ConfigureAwait(false);
        _ = _acpi.Write(AsusAcpiId.FastPower, checked((uint)fast));
    }

    private int? Scalar(AsusAcpiId id)
    {
        return AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(id), out var value) ? value : null;
    }

    private int? Watts(AsusAcpiId id)
    {
        return Scalar(id) is { } watts && watts > 0 ? watts : null;
    }

    private bool InRange(int watts)
    {
        return watts >= Minimum && watts <= Maximum;
    }

    private CapabilityCommandResult OutOfRange(CapabilityCommand command)
    {
        return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
            $"The {_model.DisplayName} accepts {Minimum}-{Maximum} W.");
    }
}

/// <summary>Fan curves over ATKACPI.</summary>
/// <remarks>
///     HC converts its curve to sixteen bytes, eight temperatures then eight duties, clamps duties to
///     99 and writes the same curve to the CPU, GPU and mid fans (<c>ROGAlly.cs:287-327</c>,
///     <c>AsusACPI.cs:281-298</c>). Turning custom control off writes HC's default tables back
///     (<c>ROGAlly.cs:466-478</c>); this plugin writes the curves it captured first when it has them.
/// </remarks>
/// <param name="acpi">Shared ATKACPI transport for fan queries and curve writes.</param>
/// <param name="delay">Cancellable spacing between fan-channel writes.</param>
internal sealed class AllyFanCapability(IAsusAcpi acpi, Func<TimeSpan, CancellationToken, Task> delay)
{
    private readonly IAsusAcpi _acpi = acpi ?? throw new ArgumentNullException(nameof(acpi));

    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? throw new ArgumentNullException(nameof(delay));

    /// <summary>Whether the mid fan curve answered with a valid curve when the service started.</summary>
    public bool HasMidFan { get; private set; }

    /// <summary>Whether the firmware answered any curve query when the service started.</summary>
    public bool CurvesReadable { get; private set; }

    internal bool HasCustomWrite { get; private set; }

    /// <summary>
    ///     Queries the mid-fan curve, and the CPU curve when that fails, for the current mode to set
    ///     <see cref="HasMidFan" /> and <see cref="CurvesReadable" />.
    /// </summary>
    public void Probe()
    {
        var mode = Mode();
        HasMidFan = ReadCurve(AsusAcpiId.MidFanCurve, mode) is not null;
        CurvesReadable = HasMidFan || ReadCurve(AsusAcpiId.CpuFanCurve, mode) is not null;
    }

    public AllyFanSnapshot Read()
    {
        var mode = Mode();
        return new AllyFanSnapshot(
            ReadCurve(AsusAcpiId.CpuFanCurve, mode),
            ReadCurve(AsusAcpiId.GpuFanCurve, mode),
            HasMidFan ? ReadCurve(AsusAcpiId.MidFanCurve, mode) : null,
            mode);
    }

    /// <summary>The CPU and GPU fan readings, in HC's duty units.</summary>
    /// <returns>CPU and GPU scalar readings, independently null when the firmware does not expose a valid scalar.</returns>
    public (int? Cpu, int? Gpu) ReadFans()
    {
        return (Scalar(AsusAcpiId.CpuFanSpeed), Scalar(AsusAcpiId.GpuFanSpeed));
    }

    public ValueTask<CapabilityCommandResult> ApplyDutyAsync(CapabilityCommand command, int percent,
        CancellationToken token)
    {
        if (percent is < 0 or > 100)
        {
            return ValueTask.FromResult(CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "Fan duty is outside 0-100 percent."));
        }

        var curve = AsusAcpiProtocol.FlatFanCurve(percent);
        return WriteAllAsync(command, curve, curve, token);
    }

    public async ValueTask<CapabilityCommandResult> ApplyCurveAsync(
        CapabilityCommand command,
        IReadOnlyList<CurvePoint> points,
        CancellationToken cancellationToken)
    {
        if (!AsusFanCurve.TryEncode(points, out var curve, out var error))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
        }

        return await WriteAllAsync(command, curve, curve,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the fans to firmware control: the captured curves, else HC's defaults.</summary>
    /// <param name="command">Admitted command whose identity is retained in the result.</param>
    /// <param name="original">
    ///     Captured CPU/GPU curves, or null for HC defaults; a missing mid-fan curve uses the selected CPU
    ///     curve.
    /// </param>
    /// <param name="cancellationToken">Cancels channel-spacing delays without reverting completed writes.</param>
    /// <returns>Unverified after the curve writes return, or indeterminate after failure; no readback or rollback occurs.</returns>
    public async ValueTask<CapabilityCommandResult> ApplyAutomaticAsync(
        CapabilityCommand command,
        AllyFanSnapshot? original,
        CancellationToken cancellationToken)
    {
        var cpu = original?.Cpu ?? AsusFanCurve.DefaultCpuCurve;
        var gpu = original?.Gpu ?? AsusFanCurve.DefaultGpuCurve;
        var result = await WriteAllAsync(command, cpu, gpu, cancellationToken, original?.Mid).ConfigureAwait(false);
        if (result.Outcome == CommandOutcome.Applied)
        {
            HasCustomWrite = false;
        }

        return result;
    }

    /// <summary>Writes HC's factory tables, the state HC returns the fans to (<c>ROGAlly.cs:466-478</c>).</summary>
    /// <param name="cancellationToken">Cancels channel-spacing delays; a partial write sequence can remain.</param>
    /// <returns>Completion after CPU/GPU and any applicable mid-fan writes; native failures and cancellation propagate.</returns>
    public async ValueTask WriteFactoryAsync(CancellationToken cancellationToken)
    {
        await WriteChannelsAsync(AsusFanCurve.DefaultCpuCurve, AsusFanCurve.DefaultGpuCurve,
            AsusFanCurve.DefaultCpuCurve, cancellationToken).ConfigureAwait(false);
        HasCustomWrite = false;
    }

    public async ValueTask RestoreAsync(AllyFanSnapshot original, CancellationToken cancellationToken)
    {
        if (!original.Readable)
        {
            throw new InvalidOperationException("The fan restore snapshot is incomplete.");
        }

        await WriteChannelsAsync(original.Cpu!, original.Gpu!, original.Mid, cancellationToken)
            .ConfigureAwait(false);
        HasCustomWrite = false;
    }

    private async ValueTask<CapabilityCommandResult> WriteAllAsync(
        CapabilityCommand command,
        byte[] cpu,
        byte[] gpu,
        CancellationToken cancellationToken,
        byte[]? mid = null)
    {
        try
        {
            HasCustomWrite = true;
            await WriteChannelsAsync(cpu, gpu, mid ?? cpu, cancellationToken).ConfigureAwait(false);
            return CommandResults.Applied(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return AllyApplied.Failed(command, "fan curve", ex, cancellationToken);
        }
    }

    private async ValueTask WriteChannelsAsync(
        byte[] cpu,
        byte[] gpu,
        byte[]? mid,
        CancellationToken cancellationToken)
    {
        _ = _acpi.WriteBuffer(AsusAcpiId.CpuFanCurve, cpu);
        await _delay(AllyPowerCapability.WriteSpacing, cancellationToken).ConfigureAwait(false);
        _ = _acpi.WriteBuffer(AsusAcpiId.GpuFanCurve, gpu);
        // HC writes the mid fan on every Ally (ROGAlly.cs:317-321). Where the probe could not tell,
        // do the same, and let a firmware without the channel refuse it without failing the rest.
        if (mid is not null && (HasMidFan || !CurvesReadable))
        {
            await _delay(AllyPowerCapability.WriteSpacing, cancellationToken).ConfigureAwait(false);
            try
            {
                _ = _acpi.WriteBuffer(AsusAcpiId.MidFanCurve, mid);
            }
            catch (Exception ex) when (!HasMidFan && ex is IOException or Win32Exception)
            {
                DeviceTrace.Change("fans", "mid-write", $"Mid fan curve refused ({ex.Message}).",
                    DeviceTraceLevel.Warn);
            }
        }
    }

    private byte[]? ReadCurve(AsusAcpiId id, int? mode)
    {
        var curve = _acpi.ReadBuffer(id, AsusAcpiProtocol.CurveSelector(mode ?? 0));
        return AsusAcpiProtocol.IsValidCurve(curve) ? curve : null;
    }

    private int? Mode()
    {
        return Scalar(AsusAcpiId.PerformanceMode);
    }

    private int? Scalar(AsusAcpiId id)
    {
        return AsusAcpiProtocol.TryDecodeScalar(_acpi.ReadStatus(id), out var value) ? value : null;
    }
}

internal static class FanModes
{
    public const string Automatic = "automatic";
    public const string Custom = "custom";
}

internal static class AllyApplied
{
    public static CapabilityCommandResult Failed(CapabilityCommand command, string operation, Exception exception,
        CancellationToken cancellationToken)
    {
        DeviceTrace.Failure(operation, "The write failed", exception);
        return CommandResults.Indeterminate(command,
            exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                ? CapabilityReasonCode.Quiescing
                : CapabilityReasonCode.TransportFaulted,
            DiagnosticText.FromException($"The {operation} write failed", exception));
    }
}
