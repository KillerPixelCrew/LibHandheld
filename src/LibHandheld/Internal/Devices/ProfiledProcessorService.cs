using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Transports;

namespace LibHandheld.Internal.Devices;

/// <summary>Processor settings for an already selected exact handheld definition.</summary>
internal sealed class ProfiledProcessorService(
    ProfiledFeatureContext featureContext,
    CpuPowerKind kind,
    int minimumGpuClock = 100,
    int maximumGpuClock = 3000,
    Func<AmdSmuTransport>? openAmd = null) : ProfiledFeatureService("processor-settings")
{
    private readonly Dictionary<string, ProcessorSetting> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Value, DateTimeOffset At)> _observed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changedWithoutOriginal = new(StringComparer.Ordinal);
    private IReadOnlyList<CapabilityDescriptor> _descriptors = [];
    private AmdSmuTransport? _amd;
    private IntelPowerTransport? _intel;
    private CapabilityDescriptor? _automaticGpu;
    private bool _descriptorsChanged;
    private CapabilityReason? _resetReason;
    private UnsupportedSettings? _unsupported;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;
    internal override bool DescriptorsChanged => _descriptorsChanged;
    public override bool Suspendable => true;

    internal override IReadOnlyList<CapabilityState> States => _descriptors.Select(descriptor =>
    {
        var observed = _observed.GetValueOrDefault(descriptor.CapabilityId);
        return ProviderControls.State(descriptor.CapabilityId, State == DeviceServiceState.Owned,
            _observed.ContainsKey(descriptor.CapabilityId) ? CapabilityValue.Integer(observed.Value) : null,
            observed.At, descriptor.CapabilityId == "processor.reset" ? _resetReason ?? Reason : Reason);
    }).ToArray();

    internal override void AcknowledgeDescriptors()
    {
        _descriptorsChanged = false;
    }

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<ProfiledCycleIdentity> context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _settings.Clear();
        _automaticGpu = null;
        var processor = featureContext.Identity.ProcessorName;
        var selectedKind = processor?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true
            ? CpuPowerKind.Intel
            : processor?.Contains("AMD", StringComparison.OrdinalIgnoreCase) == true ? CpuPowerKind.Amd : kind;
        try
        {
            if (selectedKind == CpuPowerKind.Amd)
            {
                _amd = openAmd?.Invoke() ?? AmdSmuTransport.Open(requirePowerCommands: false);
                DescribeAmd();
            }
            else
            {
                _intel = IntelPowerTransport.Open(featureContext.Resources.IntelKxPath);
                DescribeIntel();
            }

            // A setting this firmware refused before is never offered again, so it is hidden from the
            // first frame rather than vanishing when the user touches it.
            _unsupported = UnsupportedSettings.Load(featureContext.StateDirectory,
                (_amd is not null ? $"amd:{_amd.CodeName}" : "intel") + "|" + featureContext.Firmware);
            foreach (var id in _settings.Keys.Where(_unsupported.Contains).ToArray())
            {
                RemoveSetting(id);
            }

            BuildDescriptors();
            foreach (var (id, setting) in _settings)
            {
                if (featureContext.Journal.EntryFor<ProcessorSettingOriginal>(id) is { } entry
                    && entry.OriginalState.AmdCodeName != _amd?.CodeName)
                {
                    await featureContext.Journal.RetireAsync(id, "processor changed", token).ConfigureAwait(false);
                }

                await featureContext.Journal.ReconcileAsync<ProcessorSettingOriginal>(id, context.Identity.Firmware,
                    async (original, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        setting.Write(original.Value);
                        await featureContext.Journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoredUnverified, ct)
                            .ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
            }

            Observe(null, token);
            await SettleAsync(token).ConfigureAwait(false);
            return Set(DeviceServiceState.Owned);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        string firmware, CancellationToken token)
    {
        if (State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                "Processor settings access is unavailable.");
        }

        if (command.CapabilityId == "processor.reset" && command.RequestedValue is null)
        {
            return await ResetAsync(command, token).ConfigureAwait(false);
        }

        var automaticGpu = command.CapabilityId == _automaticGpu?.CapabilityId;
        var settingId = automaticGpu ? "processor.gpu-clock" : command.CapabilityId;
        if (!_settings.TryGetValue(settingId, out var setting))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                "This processor does not support the requested setting.");
        }

        if (automaticGpu ? command.RequestedValue is not null
            : command.RequestedValue is not { } requested
              || !CapabilityValueValidation.ValueMatches(requested, setting.Descriptor, out _))
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange,
                "The processor setting violates its declared range or step.");
        }

        var dispatched = false;
        try
        {
            // Recovery reads are optional. Missing observations must never disable a writable control.
            await featureContext.Journal.BeginAsync<ProcessorSettingOriginal>(settingId, firmware,
                () => setting.Read is null ? null : new ProcessorSettingOriginal
                {
                    Value = setting.Read(), AmdCodeName = _amd?.CodeName
                }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            dispatched = true;
            if (featureContext.Journal.EntryFor<ProcessorSettingOriginal>(settingId) is null)
            {
                _changedWithoutOriginal.Add(settingId);
            }

            var value = automaticGpu ? 12750 : command.RequestedValue!.IntegerValue!.Value;
            if (automaticGpu && _amd is not null)
            {
                _amd.RestoreGpuClockRange(minimumGpuClock, maximumGpuClock);
            }
            else
            {
                setting.Write(value);
            }
            if (automaticGpu)
            {
                _observed.Remove(settingId);
                _changedWithoutOriginal.Remove(settingId);
                await featureContext.Journal.SetStatusAsync(settingId, DeviceRecoveryStatus.RestoredUnverified, token)
                    .ConfigureAwait(false);
            }
            else
            {
                _observed[settingId] = (value, DateTimeOffset.UtcNow);
            }
            return CommandResults.Applied(command);
        }
        catch (Exception exception) when (AmdSmuTransport.IsDefiniteRefusal(exception))
        {
            // The firmware does not take this setting at all and nothing was applied. It is withdrawn
            // instead of being left to fail on every attempt.
            await MarkUnsupportedAsync(settingId, exception, token).ConfigureAwait(false);
            return CommandResults.Rejected(command, CapabilityReasonCode.Unsupported,
                $"This firmware does not support the setting: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (dispatched)
            {
                _observed.Remove(settingId);
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    private async ValueTask<CapabilityCommandResult> ResetAsync(CapabilityCommand command, CancellationToken token)
    {
        var dispatched = false;
        var restart = new List<string>();
        string? writing = null;
        try
        {
            foreach (var (id, setting) in _settings.ToArray())
            {
                token.ThrowIfCancellationRequested();
                var original = featureContext.Journal.PendingOriginalFor<ProcessorSettingOriginal>(id);
                // Zero removes a curve/undervolt offset. Temperatures and fixed clocks have no
                // universal default: restore the captured original or the documented automatic mode.
                var neutralOffset = id.Contains("-curve-", StringComparison.Ordinal)
                                    || id == "processor.gpu-curve"
                                    || id.EndsWith("-undervolt", StringComparison.Ordinal);
                var automaticClock = id == "processor.gpu-clock" && _automaticGpu is not null;
                if (original is null && !_changedWithoutOriginal.Contains(id)
                    && !neutralOffset && !automaticClock && setting.Read is not null)
                {
                    continue;
                }

                if (original is null && !neutralOffset && !automaticClock)
                {
                    restart.Add(setting.Descriptor.Display.CustomText ?? id);
                    _observed.Remove(id);
                    continue;
                }

                writing = id;
                dispatched = true;
                try
                {
                    if (automaticClock && _amd is not null)
                    {
                        _amd.RestoreGpuClockRange(minimumGpuClock, maximumGpuClock);
                        _observed.Remove(id);
                    }
                    else
                    {
                        var value = neutralOffset ? 0 : automaticClock ? 12750 : original!.Value;
                        setting.Write(value);
                        if (automaticClock)
                        {
                            _observed.Remove(id);
                        }
                        else
                        {
                            _observed[id] = (value, DateTimeOffset.UtcNow);
                        }
                    }
                }
                catch (Exception exception) when (AmdSmuTransport.IsDefiniteRefusal(exception))
                {
                    // Refused outright, so nothing changed and the reset goes on with the next setting.
                    writing = null;
                    await MarkUnsupportedAsync(id, exception, token).ConfigureAwait(false);
                    continue;
                }

                await featureContext.Journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoredUnverified, token)
                    .ConfigureAwait(false);
                _changedWithoutOriginal.Remove(id);
                writing = null;
            }

            _resetReason = restart.Count == 0 ? null : new CapabilityReason(CapabilityReasonCode.Unsupported,
                $"Restart Windows to restore firmware defaults for: {string.Join(", ", restart)}.");
            return CommandResults.Applied(command) with { Reason = _resetReason };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (writing is not null)
            {
                _observed.Remove(writing);
            }

            _resetReason = new CapabilityReason(CapabilityReasonCode.TransportFaulted,
                $"Processor reset did not complete: {exception.Message}. Restart Windows before tuning again.");
            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    internal override ValueTask RefreshAsync(CancellationToken token)
    {
        return RefreshAsync(null, token);
    }

    internal override ValueTask RefreshAsync(string? capabilityId, CancellationToken token)
    {
        if (State == DeviceServiceState.Owned)
        {
            Observe(capabilityId, token);
        }

        return ValueTask.CompletedTask;
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<ProfiledCycleIdentity> context, CancellationToken token)
    {
        try
        {
            if (_amd is not null || _intel is not null)
            {
                foreach (var (id, setting) in _settings)
                {
                    if (featureContext.Journal.PendingOriginalFor<ProcessorSettingOriginal>(id) is not { } original)
                    {
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    try
                    {
                        setting.Write(original.Value);
                        await featureContext.Journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoredUnverified, token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        await featureContext.Journal.SetStatusAsync(id, DeviceRecoveryStatus.RestoreFailed,
                            CancellationToken.None).ConfigureAwait(false);
                        throw;
                    }
                }
            }

            // HC has no read/restore protocol for curves, Intel undervolt, skin limits or fixed AMD clocks.
            // Retain that fact instead of inventing a zero/default restore write.
            return _changedWithoutOriginal.Count > 0
                ? Set(DeviceServiceState.ReleasedUnverified,
                    Missing("Some processor settings have no readable original and cannot be restored."))
                : Set(DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
            _observed.Clear();
        }
    }

    internal override ValueTask DisposeAsync()
    {
        _amd?.Dispose();
        _intel?.Dispose();
        _amd = null;
        _intel = null;
        return ValueTask.CompletedTask;
    }

    private void DescribeAmd()
    {
        if (_amd!.SupportsGpuClock && minimumGpuClock > 0 && maximumGpuClock >= minimumGpuClock)
        {
            Add("processor.gpu-clock", "GPU clock", minimumGpuClock, maximumGpuClock, CapabilityUnit.Megahertz,
                _amd.WriteGpuClock);
            if (_amd.SupportsAutomaticGpuClock)
            {
                _automaticGpu = new CapabilityDescriptor
                {
                    CapabilityId = "processor.gpu-clock-automatic", Role = CapabilityRole.GenericAction,
                    Display = CapabilityDescriptors.Label("Automatic GPU clock"), ValueKind = CapabilityValueKind.None,
                    SupportsAction = true, Persistence = CapabilityPersistence.Volatile
                };
            }
        }

        foreach (var (curve, id, label) in new[]
                 {
                     (AmdCurveKind.AllCores, "processor.cpu-curve-all", "CPU all-core curve offset"),
                     (AmdCurveKind.PerCore, "processor.cpu-curve-core0", "CPU core 0 curve offset"),
                     (AmdCurveKind.Gpu, "processor.gpu-curve", "GPU curve offset")
                 })
        {
            if (_amd.SupportsCurve(curve))
            {
                Add(id, label, -24, 12, CapabilityUnit.None, value => _amd.WriteCurve(curve, value), neutral: 0);
            }
        }

        foreach (var (limit, id, label) in new[]
                 {
                     (AmdThermalLimit.Tctl, "processor.tctl-limit", "CPU temperature limit"),
                     (AmdThermalLimit.Chtc, "processor.chtc-limit", "CPU hot temperature limit"),
                     (AmdThermalLimit.ApuSkin, "processor.skin-limit", "APU skin temperature limit")
                 })
        {
            if (_amd.SupportsThermalLimit(limit))
            {
                Add(id, label, 30, 120, CapabilityUnit.Celsius, value => _amd.WriteThermalLimit(limit, value),
                    _amd.SupportsThermalRead(limit) ? () => _amd.ReadThermalLimit(limit) : null);
            }
        }
    }

    private void DescribeIntel()
    {
        var intel = _intel ?? throw new InvalidOperationException("The Intel processor transport is not owned.");
        if (minimumGpuClock > 0 && maximumGpuClock >= minimumGpuClock)
        {
            Add("processor.gpu-clock", "GPU clock", minimumGpuClock, maximumGpuClock, CapabilityUnit.Megahertz,
                intel.WriteGpuClock, intel.ReadGpuClock, step: 50);
            _automaticGpu = new CapabilityDescriptor
            {
                CapabilityId = "processor.gpu-clock-automatic", Role = CapabilityRole.GenericAction,
                Display = CapabilityDescriptors.Label("Automatic GPU clock"), ValueKind = CapabilityValueKind.None,
                SupportsAction = true, Persistence = CapabilityPersistence.Volatile
            };
        }
        // HC's CPU editor writes both Core and Cache. Keep the pair on one command so neither surface
        // can report a successful CPU adjustment when only one rail was accepted.
        Add("processor.cpu-undervolt", "CPU core/cache undervolt", -50, 50, CapabilityUnit.Millivolt,
            value =>
            {
                intel.WriteUndervolt(IntelUndervoltRail.Core, value);
                intel.WriteUndervolt(IntelUndervoltRail.Cache, value);
            }, neutral: 0);
        Add("processor.gpu-undervolt", "GPU undervolt", -50, 50, CapabilityUnit.Millivolt,
            value => intel.WriteUndervolt(IntelUndervoltRail.Gpu, value), neutral: 0);
        Add("processor.system-agent-undervolt", "System Agent undervolt", -50, 50, CapabilityUnit.Millivolt,
            value => intel.WriteUndervolt(IntelUndervoltRail.SystemAgent, value), neutral: 0);
    }

    private void Add(string id, string label, int minimum, int maximum, CapabilityUnit unit,
        Action<int> write, Func<int>? read = null, int step = 1, int? neutral = null)
    {
        var descriptor = CapabilityDescriptors.Integer(id, CapabilityRole.GenericRange,
            CapabilityDescriptors.Label(label), minimum, maximum, unit, read is not null, write: true, step: step);
        _settings.Add(id, new ProcessorSetting(descriptor, write, read, neutral));
    }

    private void BuildDescriptors()
    {
        _descriptors = _settings.Values.Select(setting => setting.Descriptor)
            .Concat(_automaticGpu is { } automatic ? new[] { automatic } : Array.Empty<CapabilityDescriptor>())
            .Concat(_settings.Count > 0 ? new[]
            {
                new CapabilityDescriptor
                {
                    CapabilityId = "processor.reset", Role = CapabilityRole.GenericAction,
                    Display = CapabilityDescriptors.Label("Reset processor tuning"),
                    ValueKind = CapabilityValueKind.None, SupportsAction = true,
                    Persistence = CapabilityPersistence.Volatile
                }
            } : Array.Empty<CapabilityDescriptor>())
            .ToArray();
        _descriptorsChanged = true;
    }

    private void RemoveSetting(string id)
    {
        _settings.Remove(id);
        _observed.Remove(id);
        _changedWithoutOriginal.Remove(id);
        if (id == "processor.gpu-clock")
        {
            // The automatic range goes through the same clock commands.
            _automaticGpu = null;
        }
    }

    /// <summary>
    ///     Gives each write-only setting a known value as the service starts: its neutral value is written
    ///     and published. Readable settings were observed already, and settings without a neutral value
    ///     (fixed clocks, temperatures) stay unknown until a profile value is written.
    /// </summary>
    /// <remarks>A firmware that refuses the write outright marks the setting unsupported before anyone sees it.</remarks>
    private async ValueTask SettleAsync(CancellationToken token)
    {
        foreach (var (id, setting) in _settings.ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (setting.Read is not null || setting.Neutral is not { } neutral || _observed.ContainsKey(id))
            {
                continue;
            }

            try
            {
                setting.Write(neutral);
                _observed[id] = (neutral, DateTimeOffset.UtcNow);
            }
            catch (Exception exception) when (AmdSmuTransport.IsDefiniteRefusal(exception))
            {
                await MarkUnsupportedAsync(id, exception, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Not retried: the setting stays unknown, and hidden, until a value is written.
                DeviceTrace.Write(DeviceTraceLevel.Warn, "processor",
                    $"{id} could not be set to {neutral} at start and stays unknown: {exception.Message}");
            }
        }
    }

    private async ValueTask MarkUnsupportedAsync(string id, Exception refusal, CancellationToken token)
    {
        RemoveSetting(id);
        _unsupported?.Add(id);
        BuildDescriptors();
        if (featureContext.Journal.EntryFor<ProcessorSettingOriginal>(id) is not null)
        {
            await featureContext.Journal.RetireAsync(id, "firmware refused the setting", token).ConfigureAwait(false);
        }

        DeviceTrace.Write(DeviceTraceLevel.Warn, "processor",
            $"{id} is not supported by this firmware and is hidden until the BIOS changes: {refusal.Message}");
    }

    private void Observe(string? capabilityId, CancellationToken token)
    {
        foreach (var (id, setting) in _settings)
        {
            token.ThrowIfCancellationRequested();
            if (setting.Read is null || capabilityId is not null && capabilityId != id)
            {
                continue;
            }

            try
            {
                var value = setting.Read();
                if (id == "processor.gpu-clock" && _intel is not null && value == 12750)
                {
                    _observed.Remove(id);
                }
                else
                {
                    _observed[id] = (value, DateTimeOffset.UtcNow);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Optional observation does not erase an acknowledged write or disable the control.
            }
        }
    }

    private sealed record ProcessorSetting(CapabilityDescriptor Descriptor, Action<int> Write, Func<int>? Read,
        int? Neutral);
}

internal sealed record ProcessorSettingOriginal
{
    public required int Value { get; init; }
    public uint? AmdCodeName { get; init; }
}
