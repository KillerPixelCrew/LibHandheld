using LibHandheld.Contracts;
using LibHandheld.Internal.Windows;

namespace LibHandheld;

public sealed partial class HandheldDevice
{
    /// <summary>Reads normalized Windows machine observations without opening mutable device transports.</summary>
    /// <returns>SMBIOS, processor and present USB identity observations. Unavailable observations remain absent.</returns>
    public static DeviceIdentitySnapshot ReadCurrentIdentity()
    {
        return WindowsMachineIdentity.Read();
    }

    /// <summary>Detects the current exact supported model using read-only Windows identity observations.</summary>
    /// <returns>The supported definition, or null. An unknown machine never falls back to another family.</returns>
    public static HandheldDefinition? DetectCurrent()
    {
        return Detect(ReadCurrentIdentity());
    }

    /// <summary>Creates the current exact supported model without starting its native services.</summary>
    /// <param name="stateDirectory">Private writable recovery-state directory.</param>
    /// <param name="diagnostics">Optional native transition callback, which must not throw.</param>
    /// <returns>A device whose native services remain stopped until StartAsync.</returns>
    /// <exception cref="NotSupportedException">The current machine has no supported exact model.</exception>
    public static HandheldDevice CreateCurrent(string stateDirectory, Action<DeviceLogEntry>? diagnostics = null)
    {
        var definition = DetectCurrent() ??
                         throw new NotSupportedException(
                             "No supported handheld model matches this machine's identity.");
        return Create(definition, stateDirectory, diagnostics);
    }

    /// <summary>Creates one explicitly selected registered model without acquiring hardware.</summary>
    /// <param name="modelId">Stable model identity from Definitions.</param>
    /// <param name="stateDirectory">Private writable recovery-state directory.</param>
    /// <param name="diagnostics">Optional native transition callback, which must not throw.</param>
    /// <returns>A device for the selected model. Native startup still revalidates the machine before writes.</returns>
    /// <exception cref="ArgumentException">The supplied model is not registered.</exception>
    public static HandheldDevice Create(string modelId, string stateDirectory,
        Action<DeviceLogEntry>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var definition = Definitions.SingleOrDefault(candidate =>
                             string.Equals(candidate.Id, modelId, StringComparison.Ordinal)) ??
                         throw new ArgumentException("Unknown handheld model. Select an identity from Definitions.",
                             nameof(modelId));
        return Create(definition, stateDirectory, diagnostics);
    }
}
