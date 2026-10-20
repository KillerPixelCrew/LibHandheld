using LibHandheld.Contracts;
using LibHandheld.Internal.Input;
using LibHandheld.Internal.Lifecycle;
using LibHandheld.Internal.Runtime;
using LibHandheld.Internal.Windows;

namespace LibHandheld.Families.MsiClaw;

internal sealed class WindowsClawMcuTransport : IClawMcuTransport
{
    private volatile bool _disposed;
    private string? _mcuPath;

    /// <summary>The gate every transport operation holds; tests hold it to queue operations behind one.</summary>
    internal SemaphoreSlim Serializer { get; } = new(1, 1);

    /// <inheritdoc />
    public ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = FindMcu();
        return ValueTask.FromResult(endpoint is not null);
    }

    /// <inheritdoc />
    public async ValueTask<byte[]> ReadProfileAsync(
        ushort address,
        byte length,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (length is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        await Serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = FindMcu()
                           ?? throw new FileNotFoundException(
                               "The Claw MCU HID collection was not present.");
            await using var stream = HidDevices.OpenStream(endpoint);
            var request = CreateRequest(0x04);
            request[5] = 1;
            request[6] = checked((byte)(address >> 8));
            request[7] = checked((byte)(address & 0xFF));
            request[8] = length;
            await WriteReportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            var response = await ReadMatchingAsync(
                stream,
                report => report[0] == 0x10
                          && report[4] == 0x05
                          && report[5] == 1
                          && report[6] == (byte)(address >> 8)
                          && report[7] == (byte)(address & 0xFF),
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
            if (response[8] != length || 9 + length > response.Length)
            {
                throw new InvalidDataException("ReadProfile returned an invalid payload length.");
            }

            return [.. response.AsSpan(9, length)];
        }
        finally
        {
            Serializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteProfileAsync(
        ushort address,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (payload.Length is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        await Serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = FindMcu()
                           ?? throw new FileNotFoundException(
                               "The Claw MCU HID collection was not present.");
            await using var stream = HidDevices.OpenStream(endpoint);
            var request = CreateRequest(0x21);
            request[5] = 1;
            request[6] = checked((byte)(address >> 8));
            request[7] = checked((byte)(address & 0xFF));
            request[8] = checked((byte)payload.Length);
            payload.CopyTo(request.AsMemory(9));
            await WriteReportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            await AwaitAcknowledgementAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Serializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SyncToRomAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await Serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = FindMcu()
                           ?? throw new FileNotFoundException(
                               "The Claw MCU HID collection was not present.");
            await using var stream = HidDevices.OpenStream(endpoint);
            await WriteReportAsync(stream, CreateRequest(0x22), cancellationToken).ConfigureAwait(false);
            await AwaitAcknowledgementAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Serializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ControllerTopology> SwitchModeAsync(
        ClawControllerMode mode,
        string physicalLocation,
        Deadline deadline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mode is not (ClawControllerMode.XInput or ClawControllerMode.DirectInput))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        DeviceWriteBudget.Require(deadline, "controller re-enumeration");

        await Serializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = FindMcu()
                           ?? throw new FileNotFoundException("The Claw MCU HID collection was not present.");
            if (!HidDevices.SamePhysicalLocation(endpoint.PhysicalLocation, physicalLocation))
            {
                throw new InvalidOperationException("The MCU endpoint moved to another physical USB location.");
            }

            // Closed before the wait below: the MCU re-enumerates after the switch.
            await using (var stream = HidDevices.OpenStream(endpoint))
            {
                var request = CreateRequest(0x24);
                request[5] = (byte)mode;
                request[6] = 0;
                await WriteReportAsync(stream, request, cancellationToken).ConfigureAwait(false);
            }

            var productId = mode is ClawControllerMode.XInput
                ? ClawHardwareFacts.XInputProductId
                : ClawHardwareFacts.DirectInputProductId;
            while (!deadline.HasExpired)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Poll only approved devnode paths while firmware re-enumerates. Read the full HID
                // descriptors once Windows reports the requested product at its captured location.
                var present = HidDevices.PresentNodes(ClawHardwareFacts.UsbVendorId, [productId]);
                if (!present.Any(node => HidDevices.SamePhysicalLocation(node.PhysicalLocation, physicalLocation)))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var topology = HidEndpointEnumerator.DiscoverControllerTopology();
                if (topology is not null
                    && topology.Mode == mode
                    && topology.ProductId == productId
                    && HidDevices.SamePhysicalLocation(
                        topology.PhysicalLocation,
                        physicalLocation))
                {
                    return topology;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException(
                "The controller did not re-enumerate in the requested mode at its physical location.");
        }
        finally
        {
            Serializer.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        // An in-flight operation still owns Release, and waiters must resume to observe disposal.
        // No wait handle is created, so the managed semaphore can be left to the GC.
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Waits up to a second for the MCU's <c>0x06</c> acknowledgement. HC's <c>WriteReport</c>
    ///     waits for nothing, so a missing acknowledgement is logged and the write stands.
    /// </summary>
    private static async ValueTask AwaitAcknowledgementAsync(FileStream stream, CancellationToken cancellationToken)
    {
        try
        {
            _ = await ReadMatchingAsync(
                stream,
                report => report[0] == 0x10 && report[4] == 0x06,
                TimeSpan.FromSeconds(1),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            DeviceTrace.Change("mcu", "ack", "The MCU sent no acknowledgement within 1 s; the write stands, as in HC.");
        }
    }

    private static byte[] CreateRequest(byte command)
    {
        var request = new byte[ClawHardwareFacts.McuReportLength];
        request[0] = 0x0F;
        request[3] = 0x3C;
        request[4] = command;
        return request;
    }

    private HidCollection? FindMcu()
    {
        var endpoint = HidEndpointEnumerator.FindMcu(_mcuPath);
        _mcuPath = endpoint?.DevicePath;
        return endpoint;
    }

    private static async ValueTask WriteReportAsync(
        FileStream stream,
        byte[] report,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(report, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> ReadMatchingAsync(
        FileStream stream,
        Func<byte[], bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return await HidDevices.ReadMatchingAsync(stream, ClawHardwareFacts.McuReportLength, predicate,
            deadline.Token).ConfigureAwait(false);
    }
}

internal sealed class WindowsClawControllerSource(OemButtonLatch oemButtons)
    : IClawControllerSource
{
    private readonly Lock _gate = new();

    private readonly OemButtonLatch _oemButtons =
        oemButtons ?? throw new ArgumentNullException(nameof(oemButtons));

    private readonly SemaphoreSlim _writeSerializer = new(1, 1);
    private HidCollection? _endpoint;
    private CancellationTokenSource? _readerCancellation;
    private Task? _readerTask;
    private byte[] _rumbleReport = new byte[11];
    private FileStream? _stream;

    /// <inheritdoc />
    public ValueTask<ControllerTopology?> DiscoverAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(HidEndpointEnumerator.DiscoverControllerTopology());
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(
        ClawModel model,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(fault);
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource firstSample = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_readerTask is not null)
            {
                throw new InvalidOperationException("The DirectInput controller reader is already active.");
            }

            _endpoint = HidEndpointEnumerator.FindDirectInputGamepad(model.MeasuredControllerReport)
                        ?? throw new FileNotFoundException(
                            "The DirectInput gamepad collection was unavailable.");
            _stream = HidDevices.OpenStream(_endpoint);
            _rumbleReport = new byte[Math.Max(11, (int)_endpoint.OutputLength)];

            // Handed to the reader, which frees it when it exits.
            HidDescriptorGamepad? descriptor = null;
            if (!model.MeasuredControllerReport)
            {
                try
                {
                    descriptor = HidDescriptorGamepad.Create(_stream.SafeFileHandle);
                }
                catch
                {
                    _stream.Dispose();
                    _stream = null;
                    _endpoint = null;
                    throw;
                }

                DeviceTrace.Info("controller",
                    $"DirectInput pad decoded through its HID descriptor ({descriptor.InputLength}-byte reports).");
            }

            _readerCancellation = new CancellationTokenSource();
            _readerTask = ReadLoopAsync(
                _stream,
                descriptor,
                publish,
                firstSample,
                _readerCancellation.Token);
            _ = ObserveReaderAsync(_readerTask, fault, _readerCancellation.Token);
        }

        try
        {
            await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        Task? reader;
        FileStream? stream;
        lock (_gate)
        {
            reader = _readerTask;
            _readerCancellation?.Cancel();
            stream = _stream;
            _stream = null;
        }

        // Closing the overlapped handle is the hard stop for a pending HID read. The reader token
        // was also cancelled above and is passed into the host callback, so a callback waiting on
        // its bounded publication channel can unwind before this wait.
        // ReSharper disable once MethodHasAsyncOverload
        stream?.Dispose();

        if (reader is not null)
        {
            try
            {
                await reader.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_readerCancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Disconnection or publication rejection is a source-health failure, but teardown
                // must still close every handle so mode restoration can continue independently.
            }
        }

        var ownsWriteGate = false;
        try
        {
            try
            {
                await _writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
                ownsWriteGate = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Closing the stream below aborts an in-flight output write. Handle cleanup cannot
                // be skipped merely because the release budget expired while waiting for it.
            }

            lock (_gate)
            {
                _readerCancellation?.Dispose();
                _endpoint = null;
                _readerCancellation = null;
                _readerTask = null;
            }
        }
        finally
        {
            if (ownsWriteGate)
            {
                _writeSerializer.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteRumbleAsync(
        byte weak,
        byte strong,
        CancellationToken cancellationToken)
    {
        await _writeSerializer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FileStream? stream;
            int outputLength;
            lock (_gate)
            {
                stream = _stream;
                outputLength = _endpoint?.OutputLength ?? 0;
            }

            if (stream is null)
            {
                return;
            }

            ClawControllerCodec.WriteRumbleReport(_rumbleReport, weak, strong);
            await stream.WriteAsync(_rumbleReport.AsMemory(0, Math.Max(11, outputLength)), cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeSerializer.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task ObserveReaderAsync(
        Task reader,
        Action<Exception> fault,
        CancellationToken cancellationToken)
    {
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                fault(ex);
            }
            catch (Exception callbackException) when (callbackException is not OutOfMemoryException)
            {
                DeviceTrace.Failure("controller", "Controller-reader fault reporting failed", callbackException);
            }
        }
    }

    private async Task ReadLoopAsync(
        FileStream stream,
        HidDescriptorGamepad? descriptor,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        TaskCompletionSource firstSample,
        CancellationToken cancellationToken)
    {
        try
        {
            await ReadReportsAsync(stream, descriptor, publish, firstSample, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Freed here, after the last HidP_* call, because only the reader knows when that was. A stop
            // whose wait for the reader was cancelled no longer frees it under a decode in progress.
            descriptor?.Dispose();
        }
    }

    private async Task ReadReportsAsync(
        FileStream stream,
        HidDescriptorGamepad? descriptor,
        Func<CanonicalControllerSample, CancellationToken, ValueTask> publish,
        TaskCompletionSource firstSample,
        CancellationToken cancellationToken)
    {
        var first = true;
        var report = new byte[descriptor?.InputLength ?? 64];
        while (!cancellationToken.IsCancellationRequested)
        {
            var offset = 0;
            while (offset < report.Length)
            {
                var read = await stream.ReadAsync(report.AsMemory(offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException("The DirectInput gamepad collection disconnected.");
                }

                offset += read;
            }

            // The MCU's first report before real data is all 0xFF, measured on MS-1T52; it would read
            // as every button held and every axis at full. The descriptor path checks the whole report.
            if (first && report.AsSpan(1, descriptor is null ? 9 : report.Length - 1).IndexOfAnyExcept((byte)0xFF) < 0)
            {
                continue;
            }

            CanonicalControllerSample sample;
            if (descriptor is not null)
            {
                if (!descriptor.TryDecode(report, DateTimeOffset.UtcNow, _oemButtons, out sample))
                {
                    continue;
                }
            }
            else
            {
                sample = ClawControllerCodec.Decode(
                    report,
                    DateTimeOffset.UtcNow,
                    _oemButtons);
            }

            first = false;
            await publish(sample, cancellationToken).ConfigureAwait(false);
            firstSample.TrySetResult();
        }
    }
}

/// <summary>The Claw's MCU and pad collections, found through the SDK's HID layer.</summary>
internal static class HidEndpointEnumerator
{
    private static readonly ushort[] ProductIds =
    [
        ClawHardwareFacts.XInputProductId,
        ClawHardwareFacts.DirectInputProductId,
        ClawHardwareFacts.TestingProductId
    ];

    public static HidCollection? FindMcu(string? knownPath = null)
    {
        if (!string.IsNullOrWhiteSpace(knownPath)
            && Enumerate(knownPath).FirstOrDefault(IsMcu) is { } known)
        {
            return known;
        }

        return Enumerate().FirstOrDefault(IsMcu);
    }

    /// <summary>The DirectInput pad the MCU presents after switching to that mode.</summary>
    /// <returns>The collection, or null when it cannot be found.</returns>
    /// <remarks>
    ///     The exact PID, usage and report length distinguish the physical gamepad collection from the
    ///     MCU vendor command collection. Controller input on the reference unit is supplied through
    ///     this matching path.
    /// </remarks>
    /// <param name="measuredLayout">
    ///     Requires the measured 64-byte report. A descriptor-decoded model accepts any length.
    /// </param>
    public static HidCollection? FindDirectInputGamepad(bool measuredLayout = true)
    {
        return Enumerate().FirstOrDefault(endpoint =>
            endpoint.ProductId is ClawHardwareFacts.DirectInputProductId or ClawHardwareFacts.TestingProductId
            && endpoint is { UsagePage: 0x0001, Usage: 0x0005 }
            && (!measuredLayout || endpoint.InputLength == 64));
    }

    public static ControllerTopology? DiscoverControllerTopology()
    {
        var endpoints = Enumerate();
        var mcu = endpoints.FirstOrDefault(endpoint =>
            endpoint.ProductId is ClawHardwareFacts.XInputProductId or ClawHardwareFacts.DirectInputProductId
            && endpoint is { OutputLength: 64, UsagePage: >= 0xFF00 });
        if (mcu is null || string.IsNullOrWhiteSpace(mcu.PhysicalLocation))
        {
            return null;
        }

        var productId = mcu.ProductId;
        var mode = productId == ClawHardwareFacts.XInputProductId
            ? ClawControllerMode.XInput
            : ClawControllerMode.DirectInput;
        // XInput mode also exposes the pad through its XUSB node, which XInput itself opens; hiding only the
        // HID collection left that path visible. The node set is the one the Ally and Legion Go hide.
        IReadOnlyList<PhysicalDeviceIdentity> physical =
        [
            .. endpoints
                .Where(endpoint =>
                    endpoint.ProductId == mcu.ProductId
                    && HidDevices.SamePhysicalLocation(endpoint.PhysicalLocation, mcu.PhysicalLocation)
                    && (endpoint.InstancePath.Contains("&IG_", StringComparison.OrdinalIgnoreCase)
                        || endpoint is { UsagePage: 0x0001, Usage: 0x0005 }))
                .Select(PhysicalIdentities.FromCollection),
            .. HidDevices.PresentNodes(ClawHardwareFacts.UsbVendorId, [productId])
                .Where(node =>
                    (node.ClassGuid == HidDevices.XnaCompositeClass || node.ClassGuid == HidDevices.XboxCompositeClass)
                    && !string.IsNullOrWhiteSpace(node.PhysicalLocation)
                    && HidDevices.SamePhysicalLocation(node.PhysicalLocation, mcu.PhysicalLocation))
                .Select(PhysicalIdentities.FromNode)
        ];
        var observed = string.Join(", ", endpoints
            .Where(endpoint => HidDevices.SamePhysicalLocation(endpoint.PhysicalLocation, mcu.PhysicalLocation))
            .Select(endpoint => endpoint.Describe()));
        return new ControllerTopology(mode, productId, mcu.PhysicalLocation, physical, observed);
    }


    private static bool IsMcu(HidCollection endpoint)
    {
        // HC's hidFilters: usage page and usage per PID, nothing else.
        return endpoint.ProductId switch
        {
            ClawHardwareFacts.XInputProductId => endpoint is { UsagePage: 0xFFA0, Usage: 0x0001 },
            ClawHardwareFacts.DirectInputProductId => endpoint is { UsagePage: 0xFFF0, Usage: 0x0040 },
            _ => false
        };
    }

    private static IReadOnlyList<HidCollection> Enumerate(string? requiredPath = null)
    {
        return HidDevices.Enumerate(ClawHardwareFacts.UsbVendorId, ProductIds, requiredPath);
    }
}
