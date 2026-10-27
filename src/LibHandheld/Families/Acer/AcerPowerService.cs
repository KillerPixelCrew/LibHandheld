using System.Buffers.Binary;
using LibHandheld.Contracts;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Devices;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.Acer;

/// <summary>Atlas power profiles through the firmware's EC HID protocol.</summary>
internal sealed class AcerPowerService(ProfiledFeatureContext context, Func<IAcerEcTransport?>? open = null)
    : ProfiledFeatureService("acer-power")
{
    private const string ModeId = CapabilityIds.Scenario;
    private const string ProfileRecoveryId = "acer.manual-profile";
    private const string ModeRecoveryId = "acer.operating-mode";
    private readonly ObservedValues _observed = new();
    private IAcerEcTransport? _ec;
    private IReadOnlyList<CapabilityDescriptor> _descriptors = [];
    private byte[]? _profile;
    private int? _mode;
    private bool _unknownOriginalChanged;
    private bool _restoreUnverified;

    internal override IReadOnlyList<CapabilityDescriptor> Descriptors => _descriptors;
    internal override IReadOnlyList<CapabilityState> States => _descriptors.Select(item =>
        ProviderControls.State(item.CapabilityId, State == DeviceServiceState.Owned,
            _observed.Value(item.CapabilityId), _observed.Time(item.CapabilityId), Reason)).ToArray();
    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _observed.Clear();
        _ec = open is null ? AcerEcTransport.Open() : open();
        if (_ec is null)
        {
            return Set(DeviceServiceState.Passive, Missing("The Atlas EC HID collection is absent."));
        }

        // Data version describes the wire format, not the firmware identity. An unavailable
        // getter must not disable mode writes or retire an original bound to the captured BIOS.
        try
        {
            var versionReply = _ec.Exchange([0, 0xA0, 0, 0, 0], token);
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
            new()
            {
                CapabilityId = ModeId, Role = CapabilityRole.ScenarioMode,
                Display = new CapabilityDisplay { Key = DisplayKey.PerformanceProfile }, ValueKind = CapabilityValueKind.Choice,
                SupportsRead = true, SupportsWrite = true, Persistence = CapabilityPersistence.Volatile,
                Choices = CapabilityDescriptors.Choices(["turbo", "balanced", "quiet", "manual"])
            }
        ];
        // Limits are firmware capabilities, not desktop fallback values or guessed CPU limits.
        try
        {
            var minimum = _descriptors.Count == 0 ? ReadLimit(selector: 1, token) : 0;
            var maximum = _descriptors.Count == 0 ? ReadLimit(selector: 2, token) : 0;
            var boostMaximum = _descriptors.Count == 0 ? ReadLimit(selector: 4, token) : 0;
            if (minimum > 0 && maximum >= minimum && boostMaximum >= maximum && boostMaximum <= 200)
            {
                descriptors.Add(ProviderControls.Integer(CapabilityIds.PowerSustained,
                    CapabilityRole.PowerSustainedLimit, DisplayKey.SustainedPowerLimit, minimum, maximum,
                    CapabilityUnit.Watt, write: true) with { PairedPowerLimitId = CapabilityIds.PowerBoost });
                descriptors.Add(ProviderControls.Integer(CapabilityIds.PowerBoost, CapabilityRole.PowerSlowLimit,
                    DisplayKey.BoostPowerLimit, minimum, boostMaximum, CapabilityUnit.Watt, write: true));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"Firmware power limits unavailable: {exception.Message}");
        }

        if (_descriptors.Count == 0)
        {
            _descriptors = descriptors;
        }
        await context.Journal.ReconcileAsync<AcerProfileOriginal>(ProfileRecoveryId, context.Firmware,
            RestoreProfileAsync, token).ConfigureAwait(false);
        await context.Journal.ReconcileAsync<AcerModeOriginal>(ModeRecoveryId, context.Firmware,
            RestoreModeAsync, token).ConfigureAwait(false);
        ReadCurrent(token);
        return Set(DeviceServiceState.Owned);
    }

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

    private void ReadCurrent(CancellationToken token)
    {
        try
        {
            var reply = _ec!.Exchange([0, 0xA0, 1, 0, 2], token);
            _mode = reply[4] <= 3 ? reply[4] : null;
            _observed.Read(ModeId, _mode is { } mode ? CapabilityValue.Choice(ModeName(mode)) : null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"Mode observation unavailable: {exception.Message}");
        }

        try
        {
            var profile = _ec!.Exchange([0, 0xA0, 2, 0, 2, 3], token);
            if (profile[5] != 3)
            {
                throw new IOException("Acer manual profile selector mismatch.");
            }

            _profile = profile[..25];
            if (_mode == 3)
            {
                _observed.Read(CapabilityIds.PowerSustained,
                    CapabilityValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(6))));
                _observed.Read(CapabilityIds.PowerBoost,
                    CapabilityValue.Integer(BinaryPrimitives.ReadUInt16LittleEndian(profile.AsSpan(10))));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && exception is not OperationCanceledException)
        {
            DeviceTrace.Warn(ServiceId, $"Manual profile unavailable: {exception.Message}");
        }
    }

    internal override async ValueTask<CapabilityCommandResult> ExecuteAsync(CapabilityCommand command,
        string firmware, CancellationToken token)
    {
        if (_ec is null || State != DeviceServiceState.Owned)
        {
            return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing, "Atlas EC is unavailable.");
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
                    "turbo" => 0, "balanced" => 1, "quiet" => 2, "manual" => 3,
                    _ => throw new ArgumentException("Unknown Acer operating mode.")
                };
            }
            else
            {
                if (!DevicePowerPair.TryResolve(command, Descriptors, out var sustained, out var boost, out var error))
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.ValueOutOfRange, error!);
                }

                // These bytes carry unrelated firmware fields, so never synthesize a blank profile.
                ReadCurrent(token);

                if (_profile is null)
                {
                    return CommandResults.Rejected(command, CapabilityReasonCode.PrerequisiteMissing,
                        "The manual profile could not be retained; unrelated firmware fields cannot be preserved.");
                }

                await context.Journal.BeginAsync(ProfileRecoveryId, context.Firmware,
                    new AcerProfileOriginal { Profile = (byte[])_profile.Clone() }, token).ConfigureAwait(false);
                await CaptureModeAsync(token).ConfigureAwait(false);
                var profile = (byte[])_profile.Clone();
                BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(6), checked((ushort)sustained));
                BinaryPrimitives.WriteUInt16LittleEndian(profile.AsSpan(10), checked((ushort)boost));
                token.ThrowIfCancellationRequested();
                WriteProfile(profile, token, () => dispatched = true);
                _profile = profile;
                writtenSustained = sustained;
                writtenBoost = boost;
                selected = 3;
            }

            await CaptureModeAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SelectMode(selected, token, () => dispatched = true);
            _mode = selected;
            _observed.Written(ModeId, CapabilityValue.Choice(ModeName(selected)));
            if (writtenSustained is { } sustainedValue && writtenBoost is { } boostValue)
            {
                _observed.Written(CapabilityIds.PowerSustained, CapabilityValue.Integer(sustainedValue));
                _observed.Written(CapabilityIds.PowerBoost, CapabilityValue.Integer(boostValue));
            }
            if (selected != 3)
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
                _profile = null;
            }

            return CommandResults.TransportFailure(command, dispatched, exception);
        }
    }

    private async ValueTask CaptureModeAsync(CancellationToken token)
    {
        await context.Journal.BeginAsync<AcerModeOriginal>(ModeRecoveryId, context.Firmware,
            () =>
            {
                var reply = _ec!.Exchange([0, 0xA0, 1, 0, 2], token);
                return reply[4] <= 3 ? new AcerModeOriginal { Mode = reply[4] } : null;
            }, token).ConfigureAwait(false);
        if (!context.Journal.HoldsOriginal(ModeRecoveryId, context.Firmware))
        {
            _unknownOriginalChanged = true;
        }
    }

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

    private void SelectMode(int mode, CancellationToken token, Action? dispatched = null)
    {
        _ec!.Exchange([0, 0xA0, 1, 0, 1, checked((byte)mode)], token, dispatched);
    }

    private async ValueTask RestoreProfileAsync(AcerProfileOriginal original, CancellationToken token)
    {
        if (original.Profile.Length != 25 || original.Profile[5] != 3)
        {
            throw new IOException("Invalid retained Acer manual profile.");
        }

        WriteProfile(original.Profile, token);
        bool? verified = null;
        try
        {
            var reply = _ec!.Exchange([0, 0xA0, 2, 0, 2, 3], token);
            verified = reply.AsSpan(5, 20).SequenceEqual(original.Profile.AsSpan(5, 20));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"Restored profile observation unavailable: {exception.Message}");
        }

        _restoreUnverified |= verified != true;
        await context.Journal.SetStatusAsync(ProfileRecoveryId,
            verified == true ? DeviceRecoveryStatus.RestoredVerified
                : verified == false ? DeviceRecoveryStatus.RestoreFailed : DeviceRecoveryStatus.RestoredUnverified,
            CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask RestoreModeAsync(AcerModeOriginal original, CancellationToken token)
    {
        if (original.Mode is < 0 or > 3)
        {
            throw new IOException("Invalid retained Acer mode.");
        }

        SelectMode(original.Mode, token);
        bool? verified = null;
        try
        {
            verified = _ec!.Exchange([0, 0xA0, 1, 0, 2], token)[4] == original.Mode;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DeviceTrace.Warn(ServiceId, $"Restored mode observation unavailable: {exception.Message}");
        }

        _restoreUnverified |= verified != true;
        await context.Journal.SetStatusAsync(ModeRecoveryId,
            verified == true ? DeviceRecoveryStatus.RestoredVerified
                : verified == false ? DeviceRecoveryStatus.RestoreFailed : DeviceRecoveryStatus.RestoredUnverified,
            CancellationToken.None).ConfigureAwait(false);
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(DeviceCycleContext<ProfiledCycleIdentity> cycle,
        CancellationToken token)
    {
        try
        {
            if (_ec is not null)
            {
                await context.Journal.ReconcileAsync<AcerProfileOriginal>(ProfileRecoveryId, context.Firmware,
                    RestoreProfileAsync, token).ConfigureAwait(false);
                await context.Journal.ReconcileAsync<AcerModeOriginal>(ModeRecoveryId, context.Firmware,
                    RestoreModeAsync, token).ConfigureAwait(false);
            }

            return Set(_unknownOriginalChanged || _restoreUnverified || context.Journal.HasUnrestoredMutation(ProfileRecoveryId)
                                              || context.Journal.HasUnrestoredMutation(ModeRecoveryId)
                ? DeviceServiceState.ReleasedUnverified : DeviceServiceState.Idle);
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    internal override ValueTask DisposeAsync()
    {
        _ec?.Dispose();
        _ec = null;
        _profile = null;
        _mode = null;
        return ValueTask.CompletedTask;
    }

    private static string ModeName(int mode)
    {
        return mode switch { 0 => "turbo", 1 => "balanced", 2 => "quiet", 3 => "manual", _ => "unknown" };
    }
}

internal sealed record AcerProfileOriginal
{
    public byte[] Profile { get; init; } = [];
}

internal sealed record AcerModeOriginal
{
    public int Mode { get; init; }
}
