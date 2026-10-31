using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Families.Acer;

/// <summary>Atlas power profiles through the firmware's EC HID protocol.</summary>
/// <remarks>
///     On the measured PA08-I51 firmware, EC profile 0 is Balanced (35 W), 1 Turbo (40 W),
///     2 Quiet (30 W), and 3 Manual. The desktop application's enum labels are not this EC ordering.
///     EC command 0001 selects the profile; command 0002 reads or writes
///     profile 3 and its limits. PL1/PL2 are unsigned little-endian integer watts at payload offsets
///     6/10. The other fields in the 25-byte profile snapshot are opaque and must survive edits.
///     Maxima come from firmware selectors 2/4. The PL1 floor is the lower of selector 1 and the
///     attended 8 W. The manual profile and active mode have separate firmware-bound recovery records.
///     The user picks the power-limit path (<see cref="IntelPowerPaths" />). Firmware writes PL1/PL2 into
///     the Manual profile. PawnIO and KX open Manual once to the firmware maxima and write MSR 0x610, KX
///     also the MCHBAR mirror; Predator Sense lags in game like the EC path, and the EC never changed the
///     register (POWER_LIMIT_TESTS.md). Stock modes stay EC-owned on every path.
/// </remarks>
internal sealed class AcerPowerService(
    ProfiledFeatureContext context,
    IAcerLease<IAcerEcTransport> ec,
    Func<ICpuPowerTransport>? openCpu = null)
    : ProfiledFeatureService("acer-power")
{
    private const string ModeId = CapabilityIds.Scenario;
    private const string ProfileRecoveryId = "acer.manual-profile";
    private const string ModeRecoveryId = "acer.operating-mode";
    private const string CpuRecoveryId = "acer.cpu-power";
    private const int ManualMode = 3;

    // Selector 1 advertises 10 W, but Manual stores 8 W and the maintainer confirmed it runs on the
    // PA08-I51. Intel's Arc G3 configurable TDP also starts at 8 W.
    private const int AttendedMinimumWatts = 8;

    private readonly ObservedValues _observed = new();
    private readonly IntelPowerPaths _paths = new(context.Journal, "acer-power", hasFirmware: true);
    private IAcerEcTransport? _ec;
    private IReadOnlyList<CapabilityDescriptor> _descriptors = [];
    private int? _mode;
    private bool _unknownOriginalChanged;
    private ICpuPowerTransport? _cpu;
    private CpuPowerLimits? _cpuWritten;
    private bool _cpuDriftReported;
    private bool _manualOpened;
    private int _maximum;
    private int _boostMaximum;

    /// <summary>Creates the service over its own EC lane, for fixtures and standalone use.</summary>
    internal AcerPowerService(ProfiledFeatureContext context, Func<IAcerEcTransport?> open,
        Func<ICpuPowerTransport>? openCpu = null)
        : this(context, new AcerLease<IAcerEcTransport>(open), openCpu)
    {
    }

    /// <summary>Whether PL1/PL2 edits go to the CPU register instead of the EC's Manual profile.</summary>
    internal bool DirectCpuPower => _cpu is not null && _maximum > 0 && _paths.Direct;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;
    internal override IReadOnlyList<CapabilityState> States => _descriptors.Select(item =>
        item.CapabilityId == CapabilityIds.PowerBackend
            ? _paths.State(State == DeviceServiceState.Owned, Reason)
            : ProviderControls.State(item.CapabilityId, State == DeviceServiceState.Owned,
                _observed.Value(item.CapabilityId), _observed.Time(item.CapabilityId), Reason)).ToArray();
    public override bool Suspendable => true;

    /// <summary>Opens EC access, fixes advertised visibility on first acquisition, then reconciles prior originals.</summary>
    /// <remarks>
    ///     An unavailable current-value getter leaves observation unknown and does not gate mode writes.
    ///     Power-limit descriptors require valid advertised bounds; later resume does not add/remove them.
    ///     Recovery restores profile bytes before the original operating mode, then refreshes observations.
    /// </remarks>
    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _ec = ec.Acquire();
        if (_ec is null)
        {
            return Set(DeviceServiceState.Passive, Missing("The Atlas EC HID collection is absent."));
        }

        _cpu = OpenCpu();
        _paths.Acquire(context.Resources.IntelKxPath, _cpu is not null);
        if (_descriptors.Count == 0)
        {
            _descriptors = BuildDescriptors(token);
        }

        await context.Journal.ReconcileAsync<AcerProfileOriginal>(ProfileRecoveryId, context.Firmware,
            RestoreProfileAsync, token).ConfigureAwait(false);
        await context.Journal.ReconcileAsync<AcerModeOriginal>(ModeRecoveryId, context.Firmware,
            RestoreModeAsync, token).ConfigureAwait(false);
        await context.Journal.ReconcileAsync<AcerCpuPowerOriginal>(CpuRecoveryId, context.Firmware,
            RestoreCpuAsync, token).ConfigureAwait(false);
        await _paths.ResumeAsync(context.Firmware, token).ConfigureAwait(false);
        ReadCurrent(token);
        return Set(DeviceServiceState.Owned);
    }

    /// <summary>Opens the CPU register path; a locked register or missing module keeps the EC path.</summary>
    private ICpuPowerTransport? OpenCpu()
    {
        if (openCpu is null)
        {
            return null;
        }

        ICpuPowerTransport? cpu = null;
        try
        {
            cpu = openCpu();
            var limits = cpu.ReadLimits();
            DeviceTrace.Change(ServiceId, "cpu-power",
                $"CPU-direct power limits active: MSR 0x610 reads PL1 {limits.SustainedWatts:0.###} W, PL2 {limits.BoostWatts:0.###} W.");
            return cpu;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            cpu?.Dispose();
            DeviceTrace.Warn(ServiceId, $"CPU-direct power limits unavailable, using EC limits: {exception.Message}");
            return null;
        }
    }

    /// <summary>Re-reads the active mode and its profile limits, on the observation pass and after each command.</summary>
    /// <remarks>
    ///     Firmware can change the active profile without WSGM (mode key, power source, resume). Without
    ///     this pass the published PL1/PL2 stayed at whatever acquisition or the last command saw.
    /// </remarks>
    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        if (_ec is not null && State == DeviceServiceState.Owned)
        {
            ReadCurrent(token);
            _paths.Refresh();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Reads firmware limits once per service lifetime; resume keeps the first visibility decision.</summary>
    private List<CapabilityDescriptor> BuildDescriptors(CancellationToken token)
    {
        // Data version describes the wire format, not the firmware identity. An unavailable
        // getter must not disable mode writes or retire an original bound to the captured BIOS.
        try
        {
            var versionReply = _ec!.Exchange([0, 0xA0, 0, 0, 0], token);
            var firstVersion = BinaryPrimitives.ReadUInt16LittleEndian(versionReply.AsSpan(4));
            var secondVersion = BinaryPrimitives.ReadUInt16LittleEndian(versionReply.AsSpan(6));
            var version = secondVersion != ushort.MaxValue && (firstVersion & 0xFF) == 0 ? secondVersion : firstVersion;
            DeviceTrace.Change(ServiceId, "data-version", $"EC data version 0x{version:X4}.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"EC data version unavailable: {exception.Message}");
        }

        List<CapabilityDescriptor> descriptors =
        [
            CapabilityDescriptors.Choice(ModeId, CapabilityRole.ScenarioMode,
                new CapabilityDisplay { Key = DisplayKey.PerformanceProfile },
                CapabilityDescriptors.Choices(["turbo", "balanced", "quiet", "manual"]), read: true, write: true)
        ];
        // Maxima are firmware capabilities, not desktop fallback values or guessed CPU limits.
        try
        {
            var minimum = Math.Min(ReadLimit(selector: 1, token), AttendedMinimumWatts);
            var maximum = ReadLimit(selector: 2, token);
            var boostMaximum = ReadLimit(selector: 4, token);
            if (minimum > 0 && maximum >= minimum && boostMaximum >= maximum && boostMaximum <= 200)
            {
                _maximum = maximum;
                _boostMaximum = boostMaximum;
                descriptors.Add(ProviderControls.Integer(CapabilityIds.PowerSustained,
                    CapabilityRole.PowerSustainedLimit, DisplayKey.SustainedPowerLimit, minimum, maximum,
                    CapabilityUnit.Watt, write: true) with
                {
                    PairedPowerLimitId = CapabilityIds.PowerBoost,
                    PowerPresets =
                    [
                        ManualPreset("battery-saver", "Battery Saver", 8, 8, DevicePowerMode.BetterBattery,
                            minimum, maximum, boostMaximum),
                        ManualPreset("efficiency", "Efficiency", 12, 15, DevicePowerMode.BetterBattery,
                            minimum, maximum, boostMaximum),
                        ManualPreset("light", "Light", 15, 20, DevicePowerMode.BetterBattery,
                            minimum, maximum, boostMaximum),
                        ManualPreset("quiet", "Quiet", 20, 20, DevicePowerMode.BetterBattery,
                            minimum, maximum, boostMaximum),
                        ManualPreset("balanced", "Balanced", 25, 37, DevicePowerMode.Balanced,
                            minimum, maximum, boostMaximum),
                        ManualPreset("turbo", "Turbo", 30, 37, DevicePowerMode.BestPerformance,
                            minimum, maximum, boostMaximum),
                        new DevicePowerPreset("full-power", "Full Power", maximum, boostMaximum,
                            DevicePowerMode.BestPerformance)
                        {
                            ScenarioOnAc = "manual", ScenarioOnDc = "manual"
                        }
                    ]
                });
                descriptors.Add(ProviderControls.Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                    DisplayKey.BoostPowerLimit, minimum, boostMaximum, CapabilityUnit.Watt, write: true));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"Firmware power limits unavailable: {exception.Message}");
        }

        if (_maximum > 0 && _paths.Descriptor is { } path)
        {
            descriptors.Insert(0, path);
        }

        return descriptors;
    }

    // The stock EC profiles all keep PL2 at 65 W and their readouts did not match the platform's
    // limits, so presets write explicit PL1/PL2 through Manual. Quiet, Balanced and Turbo are
    // HandheldCompanion's Arc G3 (ClawCG3EM) table after the Claw's 20 W floor, which the Atlas
    // lacks; the lower tiers reach its 8 W floor. Full Power keeps the firmware maximum.
    private static DevicePowerPreset ManualPreset(string id, string name, int sustained, int slow,
        DevicePowerMode windowsMode, int minimum, int maximum, int boostMaximum)
    {
        var pl1 = Math.Clamp(sustained, minimum, maximum);
        return new DevicePowerPreset(id, name, pl1, Math.Clamp(slow, pl1, boostMaximum), windowsMode)
        {
            ScenarioOnAc = "manual", ScenarioOnDc = "manual"
        };
    }

    /// <summary>Queries Manual/profile 3 limit selector 1=PL1 min, 2=PL1 max, 4=PL2 max, in whole watts.</summary>
    /// <remarks>The 26-byte request puts its selector at offset 25; the reply echoes it and returns uint16 at 26.</remarks>
    private int ReadLimit(byte selector, CancellationToken token)
    {
        var request = new byte[26];
        request[1] = 0xA0;
        request[2] = 2;
        request[4] = 3;
        request[5] = 3;
        request[25] = selector;
        var reply = _ec!.Exchange(request, token);
        if (reply[25] != selector)
        {
            throw new IOException("Acer power limit selector mismatch.");
        }

        return BinaryPrimitives.ReadUInt16LittleEndian(reply.AsSpan(26));
    }

    /// <summary>Best-effort observation of the active mode and the limits of its firmware profile.</summary>
    /// <remarks>
    ///     Stock profile values come from EC replies, never an inferred wattage table. Only the active
    ///     profile is read; a PL1/PL2 edit reads the Manual bytes it preserves at the moment it needs them.
    /// </remarks>
    private void ReadCurrent(CancellationToken token)
    {
        try
        {
            _mode = ReadMode(token);
            _observed.Read(ModeId, _mode is { } mode ? CapabilityValue.Choice(ModeName(mode)) : null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"Mode observation unavailable: {exception.Message}");
        }

        try
        {
            var active = _mode is { } mode ? ReadProfile((byte)mode, token) : null;
            _observed.Read(CapabilityIds.PowerSustained, active is null ? null
                : CapabilityValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(active.AsSpan(6))));
            _observed.Read(CapabilityIds.PowerBoost, active is null ? null
                : CapabilityValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(active.AsSpan(10))));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"Active power profile unavailable: {exception.Message}");
        }

        if (_cpuWritten is { } written && _mode == ManualMode && _cpu is not null)
        {
            ReadCpu(written);
        }
    }

    /// <summary>Publishes the register's limits while it governs, and logs once if firmware changed them.</summary>
    private void ReadCpu(CpuPowerLimits written)
    {
        try
        {
            var current = _cpu!.ReadLimits();
            _observed.Read(CapabilityIds.PowerSustained, CapabilityValue.Integer((int)Math.Round(current.SustainedWatts)));
            _observed.Read(CapabilityIds.PowerBoost, CapabilityValue.Integer((int)Math.Round(current.BoostWatts)));
            var drifted = Math.Abs(current.SustainedWatts - written.SustainedWatts) > 0.5
                          || Math.Abs(current.BoostWatts - written.BoostWatts) > 0.5;
            if (drifted && !_cpuDriftReported)
            {
                _cpuDriftReported = true;
                DeviceTrace.Warn(ServiceId,
                    $"Firmware changed MSR 0x610 from PL1 {written.SustainedWatts:0.###}/PL2 {written.BoostWatts:0.###} W "
                    + $"to {current.SustainedWatts:0.###}/{current.BoostWatts:0.###} W.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"CPU power limits unavailable: {exception.Message}");
        }
    }

    /// <summary>Reads the active firmware mode; values above Manual are unknown.</summary>
    private int? ReadMode(CancellationToken token)
    {
        var mode = _ec!.Exchange([0, 0xA0, 1, 0, 2], token)[4];
        return mode <= 3 ? mode : null;
    }

    private byte[] ReadProfile(byte selector, CancellationToken token)
    {
        var profile = _ec!.Exchange([0, 0xA0, 2, 0, 2, selector], token);
        if (profile[5] != selector)
        {
            throw new IOException("Acer power profile selector mismatch.");
        }
        return profile;
    }

    /// <summary>Selects a preset, or edits a retained PL1/PL2 pair and then activates Manual mode.</summary>
    /// <remarks>
    ///     DevicePowerPair enforces descriptor bounds and PL2 at least PL1. A manual edit first journals
    ///     the profile and original mode; a preset changes only mode. Successful acknowledgements publish
    ///     requested values without mandatory readback. Once any write is dispatched, a later failure
    ///     clears cached observations and reports uncertain/partial effects instead of retrying the sequence.
    /// </remarks>
    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        string firmware, CancellationToken token)
    {
        if (_ec is null || State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing, "Atlas EC is unavailable.");
        }

        if (command.CapabilityId == CapabilityIds.PowerBackend)
        {
            return await SelectPathAsync(command, firmware, token).ConfigureAwait(false);
        }

        var dispatched = false;
        int? writtenSustained = null;
        int? writtenBoost = null;
        try
        {
            int selected;
            if (command.CapabilityId == ModeId)
            {
                selected = command.RequestedValue?.ChoiceValue switch
                {
                    "turbo" => 1, "balanced" => 0, "quiet" => 2, "manual" => 3,
                    _ => throw new ArgumentException("Unknown Acer operating mode.")
                };
                // A stock mode changes its limits without a PL1/PL2 write. Capture the selected
                // profile independently so its accepted selection can publish those limits at once.
                // Missing observation never prevents the mode command.
                try
                {
                    var profile = ReadProfile((byte)selected, token);
                    writtenSustained = BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(6));
                    writtenBoost = BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(10));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
                {
                    DeviceTrace.Warn(ServiceId, $"Selected profile limits unavailable: {exception.Message}");
                }

                await CaptureModeAsync(token).ConfigureAwait(false);
            }
            else
            {
                if (!DevicePowerPair.TryResolve(command, Descriptors, out var sustained, out var boost, out var error))
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                }

                if (DirectCpuPower)
                {
                    return await WriteCpuAsync(command, sustained, boost, () => dispatched = true, token)
                        .ConfigureAwait(false);
                }

                // These bytes carry unrelated firmware fields, so never synthesize a blank profile, and
                // never write bytes older than this edit.
                byte[]? manual = null;
                try
                {
                    manual = ReadProfile(selector: 3, token)[..25];
                }
                catch (Exception exception) when (exception is not OutOfMemoryException
                                                  && exception is not OperationCanceledException)
                {
                    DeviceTrace.Warn(ServiceId, $"Manual profile unavailable: {exception.Message}");
                }

                if (manual is null)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The manual profile could not be retained; unrelated firmware fields cannot be preserved.");
                }

                await context.Journal.BeginAsync(ProfileRecoveryId, context.Firmware,
                    new AcerProfileOriginal { Profile = (byte[])manual.Clone() }, token).ConfigureAwait(false);
                await CaptureModeAsync(token).ConfigureAwait(false);
                var profile = manual;
                BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(6), checked((ushort)sustained));
                BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(10), checked((ushort)boost));
                token.ThrowIfCancellationRequested();
                WriteProfile(profile, token, () => dispatched = true);
                writtenSustained = sustained;
                writtenBoost = boost;
                selected = 3;
            }

            token.ThrowIfCancellationRequested();
            SelectMode(selected, token, () => dispatched = true);
            _mode = selected;
            _observed.Written(ModeId, CapabilityValue.Choice(ModeName(selected)));
            if (selected != ManualMode)
            {
                // A stock mode's own limits must govern, so the registers return to their originals.
                await context.Journal.ReconcileAsync<AcerCpuPowerOriginal>(CpuRecoveryId, context.Firmware,
                    RestoreCpuAsync, token).ConfigureAwait(false);
                await _paths.RestoreMchbarAsync(context.Firmware, token).ConfigureAwait(false);
            }
            else if (_cpuWritten is { } cpu)
            {
                writtenSustained = (int)Math.Round(cpu.SustainedWatts);
                writtenBoost = (int)Math.Round(cpu.BoostWatts);
            }

            if (writtenSustained is { } sustainedValue && writtenBoost is { } boostValue)
            {
                _observed.Written(CapabilityIds.PowerSustained, CapabilityValue.Integer(sustainedValue));
                _observed.Written(CapabilityIds.PowerBoost, CapabilityValue.Integer(boostValue));
            }
            else
            {
                _observed.Forget(CapabilityIds.PowerSustained);
                _observed.Forget(CapabilityIds.PowerBoost);
            }

            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Clear();
                _mode = null;
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    /// <summary>Writes PL1/PL2 to MSR 0x610, after opening Manual to the firmware maxima once per ownership.</summary>
    /// <remarks>
    ///     The EC is written only when Manual is first opened or another mode is active, so a stream of
    ///     AutoTDP steps reaches the CPU alone. Both EC originals and the register original are journaled.
    /// </remarks>
    private async ValueTask<CapabilityCommandResult> WriteCpuAsync(CapabilityCommand command, int sustained,
        int boost, Action dispatched, CancellationToken token)
    {
        var select = _mode != ManualMode;
        if (!_manualOpened)
        {
            byte[]? manual = null;
            try
            {
                manual = ReadProfile(selector: ManualMode, token)[..25];
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                                              && exception is not OperationCanceledException)
            {
                DeviceTrace.Warn(ServiceId, $"Manual profile unavailable: {exception.Message}");
            }

            if (manual is null)
            {
                return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                    "The manual profile could not be retained; unrelated firmware fields cannot be preserved.");
            }

            await context.Journal.BeginAsync(ProfileRecoveryId, context.Firmware,
                new AcerProfileOriginal { Profile = (byte[])manual.Clone() }, token).ConfigureAwait(false);
            await CaptureModeAsync(token).ConfigureAwait(false);
            BinaryPrimitives.WriteUInt16LittleEndian(manual.AsSpan(6), checked((ushort)_maximum));
            BinaryPrimitives.WriteUInt16LittleEndian(manual.AsSpan(10), checked((ushort)_boostMaximum));
            token.ThrowIfCancellationRequested();
            WriteProfile(manual, token, dispatched);
            _manualOpened = true;
            select = true;
            DeviceTrace.Change(ServiceId, "cpu-power",
                $"EC Manual opened to {_maximum}/{_boostMaximum} W; the CPU registers carry the limits.");
        }

        if (select)
        {
            await CaptureModeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SelectMode(ManualMode, token, dispatched);
            _mode = ManualMode;
            _observed.Written(ModeId, CapabilityValue.Choice(ModeName(ManualMode)));
        }

        await CaptureCpuAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var limits = new CpuPowerLimits(sustained, boost, boost);
        dispatched();
        _cpu!.WriteLimits(limits);
        _cpuWritten = limits;
        _cpuDriftReported = false;
        await _paths.WriteMchbarAsync(sustained, boost, context.Firmware, token, dispatched).ConfigureAwait(false);
        _observed.Written(CapabilityIds.PowerSustained, CapabilityValue.Integer(sustained));
        _observed.Written(CapabilityIds.PowerBoost, CapabilityValue.Integer(boost));
        return CommandResults.Applied(command);
    }

    /// <summary>Switches the power-limit path and re-applies the current PL1/PL2 through it.</summary>
    /// <remarks>
    ///     Firmware returns MSR and MCHBAR to their originals and writes the limits into Manual again; a
    ///     direct path writes them to the registers. Limits that are not known wait for the next command.
    /// </remarks>
    private async ValueTask<CapabilityCommandResult> SelectPathAsync(CapabilityCommand command, string firmware,
        CancellationToken token)
    {
        if (!_paths.TryParse(command, out var path))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "This power-limit path is not available on this device.");
        }

        if (path == _paths.Selected)
        {
            return CommandResults.Applied(command);
        }

        var sustained = _observed.Value(CapabilityIds.PowerSustained)?.IntegerValue;
        var boost = _observed.Value(CapabilityIds.PowerBoost)?.IntegerValue;
        try
        {
            await _paths.SelectAsync(path, context.Firmware, token).ConfigureAwait(false);
            if (path == IntelPowerPath.Firmware)
            {
                await context.Journal.ReconcileAsync<AcerCpuPowerOriginal>(CpuRecoveryId, context.Firmware,
                    RestoreCpuAsync, token).ConfigureAwait(false);
                _manualOpened = false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CommandResults.TransportFailure(command, true, exception);
        }

        if (_mode == ManualMode && sustained is { } pl1 && boost is { } pl2)
        {
            var reapply = await ExecuteAsync(command with
            {
                CapabilityId = CapabilityIds.PowerSustained, RequestedValue = CapabilityValue.Integer(pl1),
                PairedPowerLimitWatts = pl2
            }, firmware, token).ConfigureAwait(false);
            if (reapply.Outcome != CommandOutcome.Applied)
            {
                DeviceTrace.Warn(ServiceId, $"Limits were not re-applied after the path change: {reapply.Reason?.Detail}");
            }
        }

        return CommandResults.Applied(command);
    }

    private async ValueTask CaptureCpuAsync(CancellationToken token)
    {
        await context.Journal.BeginAsync<AcerCpuPowerOriginal>(CpuRecoveryId, context.Firmware, () =>
        {
            var original = _cpu!.ReadLimits();
            return new AcerCpuPowerOriginal { Sustained = original.SustainedWatts, Boost = original.BoostWatts };
        }, token).ConfigureAwait(false);
        if (!context.Journal.HoldsOriginal(CpuRecoveryId, context.Firmware))
        {
            _unknownOriginalChanged = true;
        }
    }

    /// <summary>Writes the register's original limits back once; the accepted write completes the restore.</summary>
    private async ValueTask RestoreCpuAsync(AcerCpuPowerOriginal original, CancellationToken token)
    {
        var cpu = _cpu ?? throw new InvalidOperationException("MSR 0x610 is not available to restore.");
        cpu.WriteLimits(new CpuPowerLimits(original.Sustained, original.Boost, original.Boost));
        _cpuWritten = null;
        await context.Journal.CompleteRestoreAsync(CpuRecoveryId, () =>
        {
            var current = cpu.ReadLimits();
            return Math.Abs(current.SustainedWatts - original.Sustained) < 0.2
                   && Math.Abs(current.BoostWatts - original.Boost) < 0.2;
        }).ConfigureAwait(false);
    }

    /// <summary>Retains the first readable original mode; an unreadable original does not prohibit a mode write.</summary>
    /// <remarks>Unknown-original changes remain ReleasedUnverified because no restoration value can be invented.</remarks>
    private async ValueTask CaptureModeAsync(CancellationToken token)
    {
        await context.Journal.BeginAsync<AcerModeOriginal>(ModeRecoveryId, context.Firmware,
            () =>
            {
                return ReadMode(token) is { } mode ? new AcerModeOriginal { Mode = mode } : null;
            }, token).ConfigureAwait(false);
        if (!context.Journal.HoldsOriginal(ModeRecoveryId, context.Firmware))
        {
            _unknownOriginalChanged = true;
        }
    }

    /// <summary>Converts the retained profile's header to command 0002/write/profile 3, preserving all data fields.</summary>
    private void WriteProfile(byte[] retained, CancellationToken token, Action? dispatched = null)
    {
        var request = (byte[])retained.Clone();
        request[0] = 0;
        request[1] = 0xA0;
        request[2] = 2;
        request[3] = 0;
        request[4] = 1;
        request[5] = 3;
        _ec!.Exchange(request, token, dispatched);
    }

    /// <summary>Sends command 0001, operation 1, with the firmware mode byte at payload offset 5.</summary>
    private void SelectMode(int mode, CancellationToken token, Action? dispatched = null)
    {
        _ec!.Exchange([0, 0xA0, 1, 0, 1, checked((byte)mode)], token, dispatched);
    }

    /// <summary>Restores the complete retained Manual profile; the accepted write completes the restore.</summary>
    /// <remarks>The logged comparison excludes the reply header and includes profile selector plus all 19 data bytes.</remarks>
    private async ValueTask RestoreProfileAsync(AcerProfileOriginal original, CancellationToken token)
    {
        if (original.Profile.Length != 25 || original.Profile[5] != 3)
        {
            throw new IOException("Invalid retained Acer manual profile.");
        }

        WriteProfile(original.Profile, token);
        await context.Journal.CompleteRestoreAsync(ProfileRecoveryId, () =>
            _ec!.Exchange([0, 0xA0, 2, 0, 2, 3], token).AsSpan(5, 20)
                .SequenceEqual(original.Profile.AsSpan(5, 20))).ConfigureAwait(false);
    }

    /// <summary>Restores a known firmware mode once; the accepted write completes the restore.</summary>
    private async ValueTask RestoreModeAsync(AcerModeOriginal original, CancellationToken token)
    {
        if (original.Mode is < 0 or > 3)
        {
            throw new IOException("Invalid retained Acer mode.");
        }

        SelectMode(original.Mode, token);
        await context.Journal.CompleteRestoreAsync(ModeRecoveryId, () => ReadMode(token) == original.Mode)
            .ConfigureAwait(false);
    }

    /// <summary>Reconciles both originals before closing EC access, preserving incomplete recovery in the journal.</summary>
    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            if (_cpu is not null)
            {
                await context.Journal.ReconcileAsync<AcerCpuPowerOriginal>(CpuRecoveryId, context.Firmware,
                    RestoreCpuAsync, token).ConfigureAwait(false);
            }

            await _paths.RestoreAsync(context.Firmware, token).ConfigureAwait(false);

            if (_ec is not null)
            {
                await context.Journal.ReconcileAsync<AcerProfileOriginal>(ProfileRecoveryId, context.Firmware,
                    RestoreProfileAsync, token).ConfigureAwait(false);
                await context.Journal.ReconcileAsync<AcerModeOriginal>(ModeRecoveryId, context.Firmware,
                    RestoreModeAsync, token).ConfigureAwait(false);
            }

            return Set(_unknownOriginalChanged || context.Journal.HasUnrestoredMutation(ProfileRecoveryId)
                                              || context.Journal.HasUnrestoredMutation(ModeRecoveryId)
                                              || context.Journal.HasUnrestoredMutation(CpuRecoveryId)
                                              || _paths.HasUnrestored
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Closes transport and forgets caches only; restoration belongs to ReleaseAsync, not disposal.</summary>
    internal override async ValueTask DisposeAsync()
    {
        _mode = null;
        _manualOpened = false;
        _cpuWritten = null;
        _cpu?.Dispose();
        _cpu = null;
        _paths.Dispose();
        if (_ec is not null)
        {
            _ec = null;
            await ec.ReleaseAsync().ConfigureAwait(false);
        }
    }

    private static string ModeName(int mode)
    {
        return mode switch { 0 => "balanced", 1 => "turbo", 2 => "quiet", 3 => "manual", _ => "unknown" };
    }
}

/// <summary>Firmware-bound original Manual profile payload; all 25 bytes are retained for exact recovery.</summary>
internal sealed record AcerProfileOriginal
{
    public byte[] Profile { get; init; } = [];
}

/// <summary>Firmware-bound original MSR 0x610 PL1/PL2 in watts, restored before the EC records.</summary>
internal sealed record AcerCpuPowerOriginal
{
    public double Sustained { get; init; }
    public double Boost { get; init; }
}

/// <summary>Firmware-bound original active power mode, separate from the stored Manual profile.</summary>
internal sealed record AcerModeOriginal
{
    public int Mode { get; init; }
}
