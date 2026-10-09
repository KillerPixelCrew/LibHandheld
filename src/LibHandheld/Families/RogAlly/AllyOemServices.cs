// SPDX-License-Identifier: MIT

using System.ComponentModel;
using LibHandheld.Internal.Capabilities;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Plugin;
using LibHandheld.Internal.Services;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.RogAlly;

/// <summary>Front OEM buttons from the vendor collection's 0x5A input reports.</summary>
/// <param name="vendor">Shared vendor-report reader started and stopped by this service.</param>
/// <param name="host">Host OEM-event and diagnostic callbacks.</param>
/// <param name="buttons">Shared button deduplication, holds and latches.</param>
internal sealed class VendorEventService(
    IAllyVendorHid vendor,
    IDeviceSink host,
    AllyOemButtonState buttons) : DeviceService<AllyIdentityState>(AllyServiceIds.VendorEvents)
{
    private readonly DeviceReconnect _reconnect = new();
    private readonly HashSet<byte> _unmapped = [];
    private AllyModel? _model;
    private long _sequence;

    public override bool Suspendable => true;

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (context.Identity.Model is not { } model)
        {
            return Set(DeviceServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        _model = model;
        // A faulted reader keeps its task until stopped; stopping it lets this start open the collection again.
        await vendor.StopAsync(cancellationToken).ConfigureAwait(false);
        if (await vendor.StartAsync(OnEventAsync, OnFault, cancellationToken).ConfigureAwait(false))
        {
            return Set(DeviceServiceState.Owned);
        }

        // After a wake the collection comes back a few seconds late. HC waits for the device before it
        // opens its events; this takes the collection when it appears, instead of going passive and
        // leaving the short ASUS button press dead until the next restart.
        host.Trace(DeviceTraceLevel.Warn, "vendor-hid", "no vendor collection yet; waiting for it.");
        _reconnect.Start(ReopenAsync, OnReopenFailed);
        return Set(DeviceServiceState.Degraded,
            Missing("No ASUS vendor collection (FF31:0080, or one answering feature report 0x5A) was found yet."));
    }

    /// <summary>The collection dropped out; HC treats that as a removal and reopens it on arrival.</summary>
    private void OnFault(Exception exception)
    {
        var detail = DiagnosticText.FromException("The vendor event reader stopped", exception);
        host.Trace(DeviceTraceLevel.Warn, "vendor-hid", detail + "; waiting for the collection to come back.");
        _ = Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
        _reconnect.Start(ReopenAsync, OnReopenFailed);
    }

    /// <remarks>Opening the collection writes nothing, so a failed open only means it is not back yet.</remarks>
    private async ValueTask<bool> ReopenAsync(CancellationToken cancellationToken)
    {
        await vendor.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await vendor.StartAsync(OnEventAsync, OnFault, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            host.Trace(DeviceTraceLevel.Debug, "vendor-hid",
                DiagnosticText.FromException("opening the vendor collection failed", ex));
            return false;
        }

        host.Trace(DeviceTraceLevel.Info, "vendor-hid", "the vendor collection is back.");
        _ = Set(DeviceServiceState.Owned);
        return true;
    }

    private void OnReopenFailed(Exception exception)
    {
        var detail = DiagnosticText.FromException("Reopening the vendor collection failed", exception);
        host.Trace(DeviceTraceLevel.Warn, "vendor-hid", detail);
        Fault(new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        await _reconnect.StopAsync().ConfigureAwait(false);
        await vendor.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    internal ValueTask OnEventAsync(byte code, DateTimeOffset timestamp)
    {
        if (_model is null)
        {
            return ValueTask.CompletedTask;
        }

        if (AllyModels.VendorAction(_model, code) is not { } action)
        {
            // Once per code, so a tester's log names what a silent button sends.
            if (_unmapped.Add(code))
            {
                DeviceTrace.Info("vendor-hid", $"unmapped vendor event 0x{code:X2}.");
            }

            return ValueTask.CompletedTask;
        }

        // Only M2 (0xA7/0xA8) reports a release; HC press-and-releases the others (ROGAlly.cs:485-505).
        var releases = code is 0xA7 or 0xA8;
        var admitted = buttons.Admit(action.ControlId, AllyOemSource.Vendor, action.Edge, releases, timestamp);
        // One line per button edge, so a tester's log shows which transport a press arrived on.
        DeviceTrace.Info("oem", $"{action.ControlId} {action.Edge} via vendor 0x{code:X2}"
                                + (admitted ? "." : ", ignored as the keyboard's echo."));
        if (!admitted)
        {
            return ValueTask.CompletedTask;
        }

        if (releases)
        {
            buttons.Hold(AllyOemSource.Vendor, action.Button, action.Edge is OemControlEdge.Pressed);
        }
        else if (action.Button is not CanonicalButtons.None)
        {
            buttons.Latch(action.Button, timestamp);
        }

        return host.PublishOemEventAsync(
            new OemControlEvent(action.ControlId, action.Press, timestamp,
                $"asus-5a-{code:X2}-{Interlocked.Increment(ref _sequence)}", action.Edge),
            CancellationToken.None);
    }
}

/// <summary>OEM buttons the firmware sends as keyboard keys: M1/M2 and the Xbox models' front keys.</summary>
/// <param name="keyboard">Shared hook source whose watched set follows service/controller ownership.</param>
/// <param name="host">Host OEM-event and diagnostic callbacks.</param>
/// <param name="buttons">Shared state combining keyboard and vendor button contributions.</param>
internal sealed class KeyboardOemService(
    IAllyKeyboardSource keyboard,
    IDeviceSink host,
    AllyOemButtonState buttons) : DeviceService<AllyIdentityState>(AllyServiceIds.Keyboard)
{
    private readonly HashSet<uint> _down = [];
    private readonly Lock _gate = new();
    private bool _acceptingKeys;
    private IReadOnlyList<AllyKeyboardControl> _front = [];
    private bool _rearEnabled;
    private bool _rearRemapped;

    public override bool Suspendable => true;

    /// <summary>Rear keys while HC's M1/M2 table is applied: the left button sends F17, the right F18.</summary>
    /// <remarks>
    ///     HHD reads the same table bytes that way (<c>base.py:396-403</c>), and an Xbox Ally X tester whose
    ///     tables were accepted found the native assignment below swapped (2026-09-26).
    /// </remarks>
    internal static IReadOnlyList<AllyKeyboardControl> Rear { get; } =
    [
        new(AllyModels.VkF17, OemControlIds.M1, CanonicalButtons.RearPaddle1),
        new(AllyModels.VkF18, OemControlIds.M2, CanonicalButtons.RearPaddle2)
    ];

    /// <summary>Rear keys the Xbox Ally X firmware sends with no table written: left F18, right F17.</summary>
    /// <remarks>Device Lab RC73XA run 2026-09-25, <c>back-left1</c> F18 and <c>back-right1</c> F17.</remarks>
    internal static IReadOnlyList<AllyKeyboardControl> NativeRear { get; } =
    [
        new(AllyModels.VkF18, OemControlIds.M1, CanonicalButtons.RearPaddle1),
        new(AllyModels.VkF17, OemControlIds.M2, CanonicalButtons.RearPaddle2)
    ];

    public override async ValueTask<DeviceServiceResult> AcquireAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        if (context.Identity.Model is not { } model)
        {
            return Set(DeviceServiceState.Passive, Missing("The exact Ally identity no longer matches."));
        }

        _front = model.FrontKeyboardControls;
        UpdateWatch();
        if (!Watched().Any())
        {
            // No key to claim yet (a classic model without the controller tables): no system-wide hook.
            return Set(DeviceServiceState.Owned);
        }

        return await keyboard.StartAsync(OnKeyAsync, OnFault, cancellationToken).ConfigureAwait(false)
            ? Set(DeviceServiceState.Owned)
            : HookUnavailable();
    }

    public override async ValueTask<DeviceServiceResult> ReleaseAsync(
        DeviceCycleContext<AllyIdentityState> context,
        CancellationToken cancellationToken)
    {
        keyboard.Watch([]);
        ReleaseHeld();
        await keyboard.StopAsync(cancellationToken).ConfigureAwait(false);
        return Set(DeviceServiceState.Idle);
    }

    /// <summary>Claims M1/M2 while they send F-keys: with the controller tables applied, or natively.</summary>
    /// <param name="enabled">Whether the rear keys are watched.</param>
    /// <param name="remapped">Whether HC's M1/M2 table is applied, which swaps the keys' sides.</param>
    /// <param name="cancellationToken">Cancels hook installation.</param>
    /// <remarks>The hook is installed with the first watched key and removed when none is left.</remarks>
    /// <returns>Completion after watch/mapping changes and any required hook start or stop; disabling clears held rear keys.</returns>
    public async ValueTask SetRearEnabledAsync(
        bool enabled,
        bool remapped,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _rearEnabled = enabled;
            _rearRemapped = remapped;
        }

        UpdateWatch();
        if (State is DeviceServiceState.Owned)
        {
            if (enabled && !await keyboard.StartAsync(OnKeyAsync, OnFault, cancellationToken).ConfigureAwait(false))
            {
                _ = HookUnavailable();
            }
            else if (!enabled && _front.Count == 0)
            {
                await keyboard.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (!enabled)
        {
            lock (_gate)
            {
                _down.Remove(AllyModels.VkF17);
                _down.Remove(AllyModels.VkF18);
            }

            buttons.Hold(AllyOemSource.Keyboard, CanonicalButtons.RearPaddle1 | CanonicalButtons.RearPaddle2, false);
            buttons.Forget(AllyOemSource.Keyboard, OemControlIds.M1, OemControlIds.M2);
        }
    }

    internal async ValueTask OnKeyAsync(AllyKeyEvent key)
    {
        AllyKeyboardControl? control;
        bool changed;
        lock (_gate)
        {
            if (!_acceptingKeys)
            {
                return;
            }

            control = Watched().Where(item => item.VirtualKey == key.VirtualKey)
                .Select(item => (AllyKeyboardControl?)item)
                .FirstOrDefault();
            changed = key.Down ? _down.Add(key.VirtualKey) : _down.Remove(key.VirtualKey);
        }

        // A held key repeats its down edge; only the first down and the up are edges.
        if (control is not { } mapped || !changed)
        {
            return;
        }

        var edge = key.Down ? OemControlEdge.Pressed : OemControlEdge.Released;
        bool admitted;
        lock (_gate)
        {
            if (!_acceptingKeys)
            {
                return;
            }

            admitted = buttons.Admit(mapped.ControlId, AllyOemSource.Keyboard, edge, true, key.Timestamp);
            if (admitted)
            {
                buttons.Hold(AllyOemSource.Keyboard, mapped.Button, key.Down);
            }
        }

        DeviceTrace.Info("oem", $"{mapped.ControlId} {edge} via keyboard 0x{mapped.VirtualKey:X2}"
                                + (admitted ? "." : ", ignored as the vendor event's echo."));
        if (!admitted)
        {
            return;
        }

        await host.PublishOemEventAsync(
            new OemControlEvent(mapped.ControlId, OemPressKind.Short, key.Timestamp,
                $"asus-key-{mapped.VirtualKey:X2}-{key.Timestamp.UtcTicks}", edge),
            CancellationToken.None).ConfigureAwait(false);
    }

    private DeviceServiceResult HookUnavailable()
    {
        ReleaseHeld();
        keyboard.Watch([]);
        return Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted,
            "The low-level keyboard hook could not be installed."));
    }

    /// <summary>The keyboard hook stopped: its buttons are gone until the next start, nothing else is.</summary>
    private void OnFault(Exception exception)
    {
        ReleaseHeld();
        keyboard.Watch([]);
        var detail = DiagnosticText.FromException("The OEM keyboard hook stopped", exception);
        host.Trace(DeviceTraceLevel.Warn, "oem", detail);
        _ = Set(DeviceServiceState.Degraded, new CapabilityReason(CapabilityReasonCode.TransportFaulted, detail));
    }

    private void UpdateWatch()
    {
        lock (_gate)
        {
            _acceptingKeys = true;
            keyboard.Watch([.. Watched().Select(item => item.VirtualKey)]);
        }
    }

    private IEnumerable<AllyKeyboardControl> Watched()
    {
        return _rearEnabled ? _front.Concat(_rearRemapped ? Rear : NativeRear) : _front;
    }

    private void ReleaseHeld()
    {
        lock (_gate)
        {
            _down.Clear();
            _acceptingKeys = false;
            buttons.ClearSource(AllyOemSource.Keyboard);
        }
    }
}
