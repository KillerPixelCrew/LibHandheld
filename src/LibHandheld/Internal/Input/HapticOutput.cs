using System.Text.Json.Serialization;

namespace LibHandheld.Internal.Input;

/// <summary>
///     One output frame travelling from the virtual target back to the physical device.
/// </summary>
/// <remarks>
///     The whole motor state, like an input sample: a newer frame replaces an older one, so nothing needs
///     to say which target or generation it came from. A value type, because rumble streams a frame per
///     change while a game vibrates and the path that carries it allocates nothing per frame.
/// </remarks>
internal readonly record struct HapticOutputFrame
{
    /// <summary>Low-frequency motor intensity, from 0 to 1.</summary>
    public float LowFrequency { get; init; }

    /// <summary>High-frequency motor intensity, from 0 to 1.</summary>
    public float HighFrequency { get; init; }

    /// <summary>Left trigger haptic intensity, from 0 to 1, where the device supports one.</summary>
    public float LeftTrigger { get; init; }

    /// <summary>Right trigger haptic intensity, from 0 to 1, where the device supports one.</summary>
    public float RightTrigger { get; init; }

    /// <summary>When the frame was produced, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Whether this frame commands no output at all.</summary>
    public bool IsSilent =>
        LowFrequency <= 0 && HighFrequency <= 0 && LeftTrigger <= 0 && RightTrigger <= 0;

    /// <summary>
    ///     A frame that stops all output.
    /// </summary>
    /// <param name="timestamp">When the stop was issued.</param>
    /// <returns>A frame with every channel at zero.</returns>
    /// <remarks>
    ///     Rumble always needs an explicit stop path. A motor left running is not a cosmetic bug: it
    ///     keeps vibrating after the game closed, the overlay opened, or the plugin was disabled, and
    ///     nothing else will turn it off.
    /// </remarks>
    public static HapticOutputFrame Stop(DateTimeOffset timestamp)
    {
        return new HapticOutputFrame { Timestamp = timestamp };
    }
}

/// <summary>What a device does with an output channel it cannot reproduce.</summary>
/// <remarks>
///     Declared per channel so the plugin's answer is visible rather than implied. A device with two
///     rumble motors and no trigger haptics should say so; silently discarding trigger output looks
///     identical to a broken implementation from the outside.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<OutputChannelSupport>))]
internal enum OutputChannelSupport
{
    /// <summary>The device drives this channel directly.</summary>
    Native,

    /// <summary>The channel is not present and its output is discarded.</summary>
    Unsupported
}

/// <summary>What the physical device can do with output.</summary>
internal sealed record HapticCapabilities
{
    /// <summary>Support for the low-frequency motor.</summary>
    public OutputChannelSupport LowFrequency { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Support for the high-frequency motor.</summary>
    public OutputChannelSupport HighFrequency { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Support for left trigger haptics.</summary>
    public OutputChannelSupport LeftTrigger { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Support for right trigger haptics.</summary>
    public OutputChannelSupport RightTrigger { get; init; } = OutputChannelSupport.Unsupported;

    /// <summary>Highest output frame rate the device accepts, in frames per second.</summary>
    public int MaxFramesPerSecond { get; init; } = 60;

    /// <summary>Lowest intensity at which the device's motors reliably produce output, 0 to 1.</summary>
    /// <remarks>
    ///     Motor technology, declared by the plugin because the host cannot know it: a voice coil or
    ///     LRA renders arbitrarily small intensities and keeps the default of zero, while an ERM
    ///     motor does not start below roughly a third of full drive. The host maps bounded haptic
    ///     events — not continuous rumble envelopes — onto this floor so LRA-grade ticks remain
    ///     perceptible; scaling a faithful actuator by an ERM floor would be as wrong as the reverse.
    /// </remarks>
    public float MinimumStartIntensity { get; init; }

    /// <summary>Shortest pulse the device's motors can render perceptibly.</summary>
    /// <remarks>
    ///     Zero for actuators with millisecond response; ERM motors need tens of milliseconds just
    ///     to spin up. The host stretches bounded haptic events to at least this length and leaves
    ///     continuous output untouched.
    /// </remarks>
    public TimeSpan MinimumPulse { get; init; }

    /// <summary>Drops channels the device cannot reproduce, leaving the rest untouched.</summary>
    /// <param name="frame">The frame as produced by the virtual target.</param>
    /// <returns>A frame with unsupported channels zeroed and the original timestamp preserved.</returns>
    /// <remarks>
    ///     Supported intensities are neither range-clamped nor checked for finite values.
    ///     Channels are dropped without redistributing their intensity to other motors.
    /// </remarks>
    public HapticOutputFrame Clamp(HapticOutputFrame frame)
    {
        return frame with
        {
            LowFrequency = LowFrequency is OutputChannelSupport.Native ? frame.LowFrequency : 0,
            HighFrequency = HighFrequency is OutputChannelSupport.Native ? frame.HighFrequency : 0,
            LeftTrigger = LeftTrigger is OutputChannelSupport.Native ? frame.LeftTrigger : 0,
            RightTrigger = RightTrigger is OutputChannelSupport.Native ? frame.RightTrigger : 0
        };
    }
}
