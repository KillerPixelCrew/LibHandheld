using System.Globalization;
using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Services;

namespace LibHandheld.Families.MsiClaw;

/// <summary>Reviewed shared MSI identifiers and protocol addresses; per-model variations live in ClawModels.</summary>
internal static class ClawHardwareFacts
{
    public const string PackageId = "msi-claw";

    public const string Manufacturer = "MICRO-STAR INTERNATIONAL CO., LTD.";
    public const ushort UsbVendorId = 0x0DB0;
    public const ushort XInputProductId = 0x1901;
    public const ushort DirectInputProductId = 0x1902;

    /// <summary>The MCU's testing mode, which HC's <c>DClawController</c> still reads as a DirectInput pad.</summary>
    public const ushort TestingProductId = 0x1903;

    public const byte PowerSustainedAddress = 0x50;
    public const byte PowerBoostAddress = 0x51;

    /// <summary>HC's ClawBZ2EM writes its boost limit here as well; see <see cref="ClawModel.WritesFastLimit" />.</summary>
    public const byte PowerFastAddress = 0x52;

    public const byte ScenarioAddress = 0xD2;
    public const byte FanCustomAddress = 0xD4;
    public const byte FanFullSpeedAddress = 0x98;
    public const byte ChargeLimitAddress = 0xD7;

    public const int McuReportLength = 64;
    public const int WmiPackageLength = 32;

    /// <summary>A USB id in the four-digit hex form device identities and snapshots carry.</summary>
    /// <param name="id">Numeric USB vendor, product, or revision identifier.</param>
    /// <returns>Exactly four uppercase invariant hexadecimal digits, without a prefix.</returns>
    public static string Hex(ushort id)
    {
        return id.ToString("X4", CultureInfo.InvariantCulture);
    }
}

internal enum ClawControllerMode : byte
{
    Offline = 0,
    XInput = 1,
    DirectInput = 2
}

/// <summary>The cycle's SMBIOS identity and the AC line; absence of optional firmware reads is not a model mismatch.</summary>
internal sealed record ClawIdentityState
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public required DeviceIdentitySnapshot Snapshot { get; init; }

    /// <summary>
    ///     The firmware the power and fan journal entries bind to: the SMBIOS BIOS version, for example
    ///     <c>bios:E1T52IMS.114</c>. MSI ships EC updates inside its BIOS packages, so a changed BIOS is the
    ///     change the binding guards, and SMBIOS reads the same on every start. Null when the BIOS version
    ///     is unknown: a command then writes without a restore point, and an entry waits.
    /// </summary>
    public string? RecoveryBinding => Snapshot.BiosVersion is { Length: > 0 } bios ? "bios:" + bios : null;

    /// <summary>The original a power or fan entry holds, when it was captured under this cycle's BIOS.</summary>
    /// <param name="journal">The device's recovery journal.</param>
    /// <param name="serviceId">The power or fan service.</param>
    /// <returns>The recorded original, or null when there is none or this start cannot compare its binding.</returns>
    public ClawRecoveryState? OriginalFor(DeviceRecoveryJournal journal, string serviceId)
    {
        return journal.EntryFor<ClawRecoveryState>(serviceId) is { } entry
               && RecoveryBinding is { } binding
               && string.Equals(entry.FirmwareIdentity, binding, StringComparison.Ordinal)
            ? entry.OriginalState
            : null;
    }

    /// <summary>Whether the current power source is AC.</summary>
    public required bool OnAcPower { get; init; }
}

/// <summary>The EC power limits and the SHIFT scenario byte, read together.</summary>
/// <param name="SustainedWatts">PL1, EC 0x50.</param>
/// <param name="BoostWatts">PL2, EC 0x51.</param>
/// <param name="Scenario">The SHIFT scenario byte, EC 0xD2.</param>
/// <param name="FastWatts">
///     EC 0x52 on a model that writes it (the BZ2EM), when it could be read. HC never reads it, so an
///     unreadable register is null rather than a failure.
/// </param>
internal sealed record PowerPair(int SustainedWatts, int BoostWatts, byte Scenario, int? FastWatts = null);

/// <summary>Raw fan-table packages whose unknown bytes must survive read-modify-write operations.</summary>
/// <param name="DutyBuffer">Duty package including opaque firmware bytes; the owner must clone before modification.</param>
/// <param name="TemperatureBuffer">Temperature package including opaque firmware bytes; temperatures are Celsius.</param>
internal sealed record FanTable(byte[] DutyBuffer, byte[] TemperatureBuffer);

/// <summary>Both fan tables and the flags needed to restore one captured fan configuration.</summary>
/// <param name="Left">First fan channel's raw tables.</param>
/// <param name="Right">Second fan channel's raw tables.</param>
/// <param name="CustomFlag">Raw custom-curve enable byte.</param>
/// <param name="FullSpeedFlag">Raw full-speed enable byte.</param>
internal sealed record FanSnapshot(
    FanTable Left,
    FanTable Right,
    byte CustomFlag,
    byte FullSpeedFlag);

/// <summary>One combined fan and EC temperature observation.</summary>
/// <param name="LeftRpm">First fan speed in revolutions per minute.</param>
/// <param name="RightRpm">Second fan speed in revolutions per minute.</param>
/// <param name="TemperatureCelsius">Reported EC temperature in degrees Celsius.</param>
internal sealed record FanTelemetry(int LeftRpm, int RightRpm, int TemperatureCelsius);

/// <summary>Semantic projection of the current MCU lighting profile.</summary>
/// <param name="Brightness">Brightness percentage from 0 through 100.</param>
/// <param name="RightRingColor">Right ring color encoded as 0xRRGGBB.</param>
/// <param name="LeftRingColor">Left ring color encoded as 0xRRGGBB.</param>
/// <param name="ButtonsColor">Button-zone color encoded as 0xRRGGBB.</param>
internal sealed record LightingState(
    int Brightness,
    int RightRingColor,
    int LeftRingColor,
    int ButtonsColor);

/// <summary>The controller interfaces observed at one moment, and what they looked like.</summary>
/// <param name="Mode">The mode the MCU reports.</param>
/// <param name="ProductId">The USB product id the MCU enumerated as.</param>
/// <param name="PhysicalLocation">Composite USB location shared by the interfaces.</param>
/// <param name="PhysicalDevices">Interfaces WSGM may hide, which is the set the handoff needs.</param>
/// <param name="ObservedEndpoints">
///     Every candidate endpoint seen at this location, as "productId/usagePage:usage in/out". Carried so
///     a failed handoff can say what it actually found rather than only that it found nothing. A plugin
///     can include this snapshot in a failure reason without another hardware enumeration.
/// </param>
internal sealed record ControllerTopology(
    ClawControllerMode Mode,
    ushort ProductId,
    string PhysicalLocation,
    IReadOnlyList<PhysicalDeviceIdentity> PhysicalDevices,
    string ObservedEndpoints = "");

/// <summary>Serializes reviewed MSI_ACPI getter and setter calls against the fixed provider instance.</summary>
/// <remarks>Cancellation bounds waiting; an in-flight native call retains the lane until it actually exits.</remarks>
internal interface IMsiWmiTransport : IAsyncDisposable
{
    /// <summary>Invokes a reviewed getter and checks its returned success status.</summary>
    /// <param name="methodName">Package-selected MSI_ACPI method beginning with Get_; never user input.</param>
    /// <param name="selector">First byte of the otherwise zero-filled 32-byte request.</param>
    /// <param name="cancellationToken">Cancels waiting; native execution may continue after cancellation.</param>
    /// <returns>The getter's returned data package, owned by the caller.</returns>
    ValueTask<byte[]> InvokeGetterAsync(
        string methodName,
        byte selector,
        CancellationToken cancellationToken);

    /// <summary>Invokes a reviewed setter; a returned provider status is not interpreted as readback.</summary>
    /// <param name="methodName">Package-selected MSI_ACPI method beginning with Set_; never user input.</param>
    /// <param name="package">Exactly 32 bytes; keep unchanged until completion.</param>
    /// <param name="cancellationToken">Cancels waiting without proving that a dispatched write stopped.</param>
    /// <returns>Completion after invocation; a failure after dispatch leaves the hardware effect uncertain.</returns>
    ValueTask InvokeSetterAsync(
        string methodName,
        byte[] package,
        CancellationToken cancellationToken);
}

/// <summary>Owns the MSI_Event subscription used to identify physical OEM button events.</summary>
internal interface IMsiOemEventSource : IAsyncDisposable
{
    /// <summary>Attempts HC's MSI_Event class repair before acquiring services.</summary>
    /// <param name="cancellationToken">Cancels repair waiting; ordinary repair failures are diagnosed.</param>
    /// <returns>Completion after the repair attempt, without promising that events are available.</returns>
    ValueTask EnsureEventClassAsync(CancellationToken cancellationToken);

    /// <summary>Subscribes to the reviewed firmware event stream.</summary>
    /// <param name="callback">Receives the firmware event byte and UTC arrival time; do not block event delivery.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>True when the subscription is active; false when it cannot be established.</returns>
    ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        CancellationToken cancellationToken);

    /// <summary>Removes the event subscription and releases its watcher.</summary>
    /// <param name="cancellationToken">Cancels admission before teardown begins.</param>
    /// <returns>Completion after local watcher cleanup; no hardware restoration is implied.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Owns serialized MCU profile and controller-mode exchanges through reviewed HID reports.</summary>
/// <remarks>Only callers with a matched model and live service ownership may mutate through this transport.</remarks>
internal interface IClawMcuTransport : IAsyncDisposable
{
    /// <summary>Looks for a supported MCU collection without retaining an open stream.</summary>
    /// <param name="cancellationToken">Cancels the lookup before enumeration.</param>
    /// <returns>True when an eligible MCU collection is present.</returns>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Reads a bounded MCU profile range and validates the matching reply.</summary>
    /// <param name="address">Model-selected 16-bit MCU address.</param>
    /// <param name="length">Number of profile bytes, from 1 through 32.</param>
    /// <param name="cancellationToken">Cancels gate acquisition or report I/O.</param>
    /// <returns>A new array containing exactly the requested profile bytes.</returns>
    ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken);

    /// <summary>Writes one bounded MCU profile range without requiring confirming readback.</summary>
    /// <param name="address">Model-selected 16-bit MCU address.</param>
    /// <param name="payload">One through 32 bytes; retain unchanged until completion.</param>
    /// <param name="cancellationToken">Cancels waiting or I/O; cancellation after dispatch is not a rollback.</param>
    /// <returns>Completion after the write and bounded acknowledgment attempt; a missing acknowledgment alone is tolerated.</returns>
    ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken);

    /// <summary>Commits written profiles to MCU ROM using HC's SyncToROM report.</summary>
    /// <param name="cancellationToken">Cancels waiting or I/O; a dispatched persistent write must not be retried blindly.</param>
    /// <returns>Completion after the write and bounded acknowledgment attempt, without readback verification.</returns>
    ValueTask SyncToRomAsync(CancellationToken cancellationToken);

    /// <summary>Requests one controller mode change and observes re-enumeration at the same USB location.</summary>
    /// <param name="mode">XInput or DirectInput; Offline is not a writable mode.</param>
    /// <param name="physicalLocation">Previously observed composite USB location that must still match.</param>
    /// <param name="deadline">Active-time budget for admission and topology observation.</param>
    /// <param name="cancellationToken">Cancels waiting; a sent mode change can still complete.</param>
    /// <returns>The observed matching topology; timeout does not prove the switch failed.</returns>
    ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        Deadline deadline,
        CancellationToken cancellationToken);
}

/// <summary>Owns the DirectInput reader and physical rumble output after controller handoff.</summary>
internal interface IClawControllerSource : IAsyncDisposable
{
    /// <summary>Observes the currently enumerated supported controller topology.</summary>
    /// <param name="cancellationToken">Cancels before discovery.</param>
    /// <returns>Observed topology, or null when no supported topology is present.</returns>
    ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>Opens the model's DirectInput collection and waits for the first canonical sample.</summary>
    /// <param name="model">Matched model selecting measured or descriptor-based report decoding.</param>
    /// <param name="publish">Receives complete samples on the reader path; keep delivery bounded.</param>
    /// <param name="fault">Reports an unexpected reader termination after startup.</param>
    /// <param name="cancellationToken">Cancels startup; the source owns the continuing reader token.</param>
    /// <returns>Completion once the first sample is delivered, or a failed task when acquisition fails.</returns>
    ValueTask StartAsync(
        ClawModel model,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    /// <summary>Requests reader cancellation, closes its stream and waits for bounded cleanup.</summary>
    /// <param name="cancellationToken">Cancels the wait, without proving the reader finished.</param>
    /// <returns>Completion after stream closure and local cleanup; a canceled wait does not prove all callbacks finished.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Writes physical motor levels through the acquired controller collection.</summary>
    /// <param name="weak">High-frequency motor level, from 0 through 255.</param>
    /// <param name="strong">Low-frequency motor level, from 0 through 255.</param>
    /// <param name="cancellationToken">Cancels serialized output delivery.</param>
    /// <returns>Completion after the report is sent, or immediately when no output stream is active.</returns>
    ValueTask WriteRumbleAsync(byte weak, byte strong, CancellationToken cancellationToken);
}

/// <summary>Owns the model-selected IMU stream and preserves calibration across stream restarts.</summary>
internal interface IClawMotionSource : IAsyncDisposable
{
    /// <summary>Acquires a gyroscope with optional accelerometer and publishes canonical motion.</summary>
    /// <param name="model">Matched model selecting sensor sources and axis signs.</param>
    /// <param name="publish">Receives samples on the sensor thread; must return quickly and not stop the source inline.</param>
    /// <param name="cancellationToken">Cancels admission before native sensor acquisition.</param>
    /// <returns>True when streaming is active; false when no usable gyroscope exists.</returns>
    ValueTask<bool> StartAsync(
        ClawModel model,
        Action<MotionSample> publish,
        CancellationToken cancellationToken);

    /// <summary>Suppresses new delivery and starts bounded disposal of the sensor stream.</summary>
    /// <param name="cancellationToken">Cancels waiting for cleanup, not native cleanup itself.</param>
    /// <returns>Completion after cleanup; timeout may leave native owners retained until callbacks finish.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Suppresses known firmware keyboard chords while publishing their semantic identity.</summary>
internal interface IFirmwareChordSuppressor : IAsyncDisposable
{
    /// <summary>Installs the chord hook on its owned message-loop thread.</summary>
    /// <param name="fault">Receives unexpected hook termination.</param>
    /// <param name="chord">Receives recognized firmware chords; must return promptly.</param>
    /// <param name="cancellationToken">Cancels installation waiting and requests cleanup.</param>
    /// <returns>True when suppression is installed, false when installation fails.</returns>
    ValueTask<bool> StartAsync(
        Action<Exception> fault,
        Action<FirmwareChord> chord,
        CancellationToken cancellationToken);

    /// <summary>Removes suppression and waits for the hook thread to end.</summary>
    /// <param name="cancellationToken">Cancels shutdown waiting; cleanup can continue after cancellation.</param>
    /// <returns>Completion after the hook thread ends; timeout reports incomplete cleanup.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Instance-owned hardware dependencies composed by ClawDeviceEngine and replaced by fakes in tests.</summary>
/// <param name="Wmi">Serialized power, fan, charge and telemetry transport.</param>
/// <param name="OemEvents">Attributed MSI firmware event source.</param>
/// <param name="Mcu">MCU profiles and controller mode transport.</param>
/// <param name="Controller">Physical controller input and motor output owner.</param>
/// <param name="Motion">Canonical IMU stream owner.</param>
/// <param name="ChordSuppressor">Known firmware chord suppression owner.</param>
/// <param name="OemButtons">Shared latch merging OEM presses into controller samples.</param>
/// <param name="Delay">Cancelable firmware pacing delay; tests may replace wall-time waiting.</param>
/// <param name="OnAcPower">Optional read-only power-source callback for an injected hardware fixture.</param>
/// <param name="Time">Optional clock for the TDP watchdog's ten-second window; the system clock by default.</param>
internal sealed record ClawHardwareServices(
    IMsiWmiTransport Wmi,
    IMsiOemEventSource OemEvents,
    IClawMcuTransport Mcu,
    IClawControllerSource Controller,
    IClawMotionSource Motion,
    IFirmwareChordSuppressor ChordSuppressor,
    OemButtonLatch OemButtons,
    Func<TimeSpan, CancellationToken, Task> Delay,
    Func<bool>? OnAcPower = null,
    TimeProvider? Time = null);
