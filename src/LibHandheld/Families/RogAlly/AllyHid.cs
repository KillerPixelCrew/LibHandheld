using System.ComponentModel;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;

// SPDX-License-Identifier: MIT

namespace LibHandheld.Families.RogAlly;

/// <summary>The controller's device nodes, which WSGM hides while it drives the virtual pad.</summary>
internal static class AllyControllerNodes
{
    /// <summary>Every present device node of the controller that WSGM must hide while it owns it.</summary>
    /// <remarks>
    ///     The XInput pad is an XUSB node (HC's XUSB arrival handler matches VID 0B05 with PID 1ABE or
    ///     1B4C, <c>ControllerManager.cs:2236-2243</c>) and possibly a GIP node on the Xbox models, plus
    ///     the XInput-compatible HID collection whose instance path carries <c>&amp;IG_</c>. The vendor,
    ///     keyboard and lighting collections are not hidden: the plugin itself reads them.
    /// </remarks>
    /// <param name="productIds">Product IDs admitted for this model under the ASUS vendor ID.</param>
    /// <returns>
    ///     Present XUSB, Xbox composite and XInput-compatible HID nodes; vendor, keyboard and lighting collections are
    ///     excluded.
    /// </returns>
    public static IReadOnlyList<DeviceNode> Find(IReadOnlyCollection<ushort> productIds)
    {
        return
        [
            .. HidDevices.PresentNodes(AllyModels.AsusVendorId, productIds).Where(node =>
                node.ClassGuid == HidDevices.XnaCompositeClass
                || node.ClassGuid == HidDevices.XboxCompositeClass
                || (node.ClassGuid == HidDevices.HidClass
                    && node.InstancePath.Contains("&IG_", StringComparison.OrdinalIgnoreCase)))
        ];
    }
}

internal interface IAllyVendorHid : IAsyncDisposable
{
    /// <summary>Finds the collection. False when this model exposes none.</summary>
    /// <param name="cancellationToken">Cancels admission before synchronous collection discovery.</param>
    /// <returns>Whether a usable collection was found; false also covers current unavailability.</returns>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Starts reading 0x5A input reports; the callback receives the event code byte.</summary>
    /// <remarks>The fault callback runs once if the reader stops on its own, such as when the MCU re-enumerates.</remarks>
    /// <param name="callback">Sequential reader callback receiving each nonzero vendor event and its observation time.</param>
    /// <param name="fault">Reader failure callback; avoid blocking or throwing from it.</param>
    /// <param name="cancellationToken">
    ///     Cancels admission before opening the stream; StopAsync owns the running-reader
    ///     lifetime.
    /// </param>
    /// <returns>True when already reading or a reader was started; false when no readable vendor collection is available.</returns>
    ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken);

    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>Sends one 0x5A configuration feature report, as HC's <c>ConfigureController</c> does.</summary>
    /// <param name="report">Feature report beginning with 0x5A, normally a 64-byte controller table.</param>
    /// <param name="cancellationToken">Cancels waiting for serialized write admission; an accepted native write is not undone.</param>
    /// <returns>Completion after one feature write; unavailable transport or native refusal throws without retry.</returns>
    ValueTask WriteConfigurationAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);
}

/// <summary>The Aura lighting collection HC drives with report 0x5D.</summary>
internal interface IAllyAuraHid : IAsyncDisposable
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);

    ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);

    ValueTask WriteOutputAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken);

    /// <summary>Puts the Windows Dynamic Lighting lamp array in autonomous mode, as HHD does.</summary>
    /// <param name="cancellationToken">Cancels waiting for the serialized lighting-write gate.</param>
    /// <returns>True after one autonomous-mode request, false when no compatible lamp array exists; native failures propagate.</returns>
    ValueTask<bool> DisableDynamicLightingAsync(CancellationToken cancellationToken);
}

/// <summary>Windows transport for the ASUS vendor collection that answers feature report 0x5A.</summary>
/// <remarks>
///     HC selects the collection whose feature report 0x5A reads (<c>ROGAlly.cs:416-436</c>), and so does this.
///     Configuration uses feature reports as HC does (<c>ROGAlly.cs:646-668</c>).
/// </remarks>
/// <param name="productIds">
///     Model-specific ASUS controller product IDs searched for a collection answering feature report
///     0x5A.
/// </param>
internal sealed class WindowsAllyVendorHid(IReadOnlyCollection<ushort> productIds) : IAllyVendorHid
{
    private readonly Lock _gate = new();
    private readonly IReadOnlyCollection<ushort> _productIds = productIds;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private HidCollection? _endpoint;
    private CancellationTokenSource? _readCancellation;
    private Task? _reader;
    private FileStream? _stream;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Find() is not null);
    }

    public ValueTask<bool> StartAsync(
        Func<byte, DateTimeOffset, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(fault);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_reader is not null)
            {
                return ValueTask.FromResult(true);
            }

            var endpoint = Find();
            if (endpoint is null || endpoint.InputLength < 2)
            {
                return ValueTask.FromResult(false);
            }

            _stream = HidDevices.OpenStream(endpoint);
            _readCancellation = new CancellationTokenSource();
            _reader = ReadLoopAsync(_stream, endpoint.InputLength, callback, fault, _readCancellation.Token);
            return ValueTask.FromResult(true);
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task? reader;
        lock (_gate)
        {
            reader = _reader;
            _readCancellation?.Cancel();
            _stream?.Dispose();
            _stream = null;
            _reader = null;
        }

        if (reader is not null)
        {
            try
            {
                await reader.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closing the handle is the hard stop for a pending read.
            }
        }

        lock (_gate)
        {
            _readCancellation?.Dispose();
            _readCancellation = null;
            // The collection can re-enumerate across suspend; the next start finds it again.
            _endpoint = null;
        }
    }

    public async ValueTask WriteConfigurationAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        if (report.Length == 0 || report.Span[0] != AllyProtocol.VendorReportId)
        {
            throw new InvalidOperationException("Only report 0x5A configuration is written to the vendor collection.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = Find() ?? throw new FileNotFoundException("The ASUS vendor collection is not present.");
            try
            {
                using var handle = HidDevices.Open(endpoint, false);
                HidDevices.SetFeature(handle, endpoint, report.Span);
            }
            catch (Win32Exception)
            {
                // Not retried: the next caller rediscovers the collection, this write stays failed.
                lock (_gate)
                {
                    _endpoint = null;
                }

                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>The vendor collection, chosen exactly as HC chooses it.</summary>
    /// <remarks>
    ///     HC's <c>IsReady</c> (<c>ROGAlly.cs:416-436</c>) walks every ASUS collection with 64-byte feature
    ///     reports (<c>IDevice.GetHidDevices(..., 64)</c>) and keeps the last one whose feature report 0x5A
    ///     reads; it reads the button events from and writes the tables to that collection. No usage is
    ///     assumed: collections differ between models, which is why HC probes.
    /// </remarks>
    private HidCollection? Find()
    {
        lock (_gate)
        {
            if (_endpoint is not null)
            {
                return _endpoint;
            }

            var endpoints = HidDevices.Enumerate(AllyModels.AsusVendorId, _productIds);
            var answering = endpoints
                .Where(endpoint => endpoint.FeatureLength >= AllyProtocol.ConfigurationLength
                                   && HidDevices.AnswersFeature(endpoint, AllyProtocol.VendorReportId))
                .ToArray();
            _endpoint = answering.LastOrDefault();
            if (_endpoint is not null)
            {
                DeviceTrace.Info("vendor-hid",
                    $"vendor collection {_endpoint.Describe()} (answering 0x5A: "
                    + $"{string.Join(", ", answering.Select(endpoint => endpoint.Describe()))}).");
            }

            return _endpoint;
        }
    }

    private static async Task ReadLoopAsync(
        FileStream stream,
        int reportLength,
        Func<byte, DateTimeOffset, ValueTask> callback,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        var report = new byte[reportLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(report, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (IOException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HC treats a failed read as the device being removed (ROGAlly.cs:364-378).
                DeviceTrace.Failure("vendor-hid", "vendor collection read failed", ex);
                fault(ex);
                return;
            }

            if (read == 0)
            {
                DeviceTrace.Warn("vendor-hid", "the vendor collection closed.");
                if (!cancellationToken.IsCancellationRequested)
                {
                    fault(new IOException("The ASUS vendor collection closed."));
                }

                return;
            }

            if (AllyProtocol.TryReadVendorEvent(report.AsSpan(0, read), out var code))
            {
                try
                {
                    await callback(code, DateTimeOffset.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DeviceTrace.Failure("vendor-hid", "OEM event publication failed", ex);
                }
            }
        }
    }
}

/// <summary>Windows transport for the Aura collection and, on the Xbox models, the lamp array.</summary>
/// <param name="productIds">Model-specific ASUS product IDs searched for Aura and lamp-array collections.</param>
internal sealed class WindowsAllyAuraHid(IReadOnlyCollection<ushort> productIds) : IAllyAuraHid
{
    private readonly Lock _gate = new();
    private readonly IReadOnlyCollection<ushort> _productIds = productIds;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private HidCollection? _aura;

    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Find() is not null);
    }

    public ValueTask SetFeatureAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        return WriteAsync(report, true, cancellationToken);
    }

    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken)
    {
        return WriteAsync(report, false, cancellationToken);
    }

    public async ValueTask<bool> DisableDynamicLightingAsync(CancellationToken cancellationToken)
    {
        // HHD writes 06 01 to the lamp array (application 0x00590001) on wake so Windows Dynamic
        // Lighting stops overriding Aura (rog_ally/base.py:482-505). Report 6 is the HID Lighting and
        // Illumination LampArrayControlReport, whose first field is AutonomousMode. The specification
        // makes it a Feature report and the RC73XA lab run saw only feature reports on page 0x59, while
        // HHD's hidraw write is an output report. One report is sent: as a feature when the collection
        // has one, otherwise as output.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lamp = HidDevices.Unique(HidDevices.Enumerate(AllyModels.AsusVendorId, _productIds),
                endpoint => endpoint is { UsagePage: 0x0059, Usage: 0x0001 }
                            && (endpoint.FeatureLength >= 2 || endpoint.OutputLength >= 2));
            if (lamp is null)
            {
                return false;
            }

            using var handle = HidDevices.Open(lamp, false);
            if (lamp.FeatureLength >= 2)
            {
                HidDevices.SetFeature(handle, lamp, [0x06, 0x01]);
            }
            else
            {
                HidDevices.WriteOutput(handle, lamp, [0x06, 0x01]);
            }

            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private async ValueTask WriteAsync(ReadOnlyMemory<byte> report, bool feature, CancellationToken cancellationToken)
    {
        if (report.Length == 0 || report.Span[0] != AllyProtocol.AuraReportId)
        {
            throw new InvalidOperationException("Only report 0x5D is written to the Aura collection.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var endpoint = Find() ?? throw new FileNotFoundException("The Aura collection is not present.");
            try
            {
                using var handle = HidDevices.Open(endpoint, false);
                if (feature)
                {
                    HidDevices.SetFeature(handle, endpoint, report.Span);
                }
                else
                {
                    HidDevices.WriteOutput(handle, endpoint, report.Span);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException)
            {
                // Not retried: the next command searches for the collection again.
                lock (_gate)
                {
                    _aura = null;
                }

                throw;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private HidCollection? Find()
    {
        lock (_gate)
        {
            if (_aura is not null)
            {
                return _aura;
            }

            // HC's IsReady checks 0x5A first and 0x5D only on a collection that did not answer 0x5A.
            _aura = HidDevices.Enumerate(AllyModels.AsusVendorId, _productIds).FirstOrDefault(endpoint =>
                endpoint.OutputLength > 0
                && !HidDevices.AnswersFeature(endpoint, AllyProtocol.VendorReportId)
                && HidDevices.AnswersFeature(endpoint, AllyProtocol.AuraReportId));
            DeviceTrace.Info("lighting", _aura is null
                ? "no collection answered Aura report 0x5D."
                : $"Aura collection {_aura.Describe()}.");
            return _aura;
        }
    }
}
