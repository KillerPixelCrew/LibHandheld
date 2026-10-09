namespace LibHandheld.Contracts;

/// <summary>
///     Every button a handheld may physically have.
/// </summary>
/// <remarks>
///     The canonical state represents the richest supported handheld without assuming a virtual target.
///     A target consumes only what it genuinely supports: the Steam Deck composite takes rear paddles and
///     native motion, Xbox 360 takes neither, and nothing is synthesized to fill the gap. Gyro is passed
///     through where the target supports motion and simply absent where it does not â€” it is never
///     converted into stick or mouse movement, which is the line between calibration and remapping.
/// </remarks>
[Flags]
public enum CanonicalButtons : uint
{
    /// <summary>Nothing pressed.</summary>
    None = 0,

    /// <summary>South face button.</summary>
    A = 1 << 0,

    /// <summary>East face button.</summary>
    B = 1 << 1,

    /// <summary>West face button.</summary>
    X = 1 << 2,

    /// <summary>North face button.</summary>
    Y = 1 << 3,

    /// <summary>Left shoulder.</summary>
    LeftShoulder = 1 << 4,

    /// <summary>Right shoulder.</summary>
    RightShoulder = 1 << 5,

    /// <summary>Left stick click.</summary>
    LeftStick = 1 << 6,

    /// <summary>Right stick click.</summary>
    RightStick = 1 << 7,

    /// <summary>View or Back.</summary>
    View = 1 << 8,

    /// <summary>Menu or Start.</summary>
    Menu = 1 << 9,

    /// <summary>Guide or Home.</summary>
    Guide = 1 << 10,

    /// <summary>D-pad up.</summary>
    DPadUp = 1 << 11,

    /// <summary>D-pad down.</summary>
    DPadDown = 1 << 12,

    /// <summary>D-pad left.</summary>
    DPadLeft = 1 << 13,

    /// <summary>D-pad right.</summary>
    DPadRight = 1 << 14,

    /// <summary>First rear paddle.</summary>
    RearPaddle1 = 1 << 15,

    /// <summary>Second rear paddle.</summary>
    RearPaddle2 = 1 << 16,

    /// <summary>Third rear paddle.</summary>
    RearPaddle3 = 1 << 17,

    /// <summary>Fourth rear paddle.</summary>
    RearPaddle4 = 1 << 18,

    /// <summary>Left stick capacitive touch.</summary>
    LeftStickTouch = 1 << 19,

    /// <summary>Right stick capacitive touch.</summary>
    RightStickTouch = 1 << 20,

    /// <summary>Left trackpad capacitive touch.</summary>
    LeftPadTouch = 1 << 21,

    /// <summary>Right trackpad capacitive touch.</summary>
    RightPadTouch = 1 << 22,

    /// <summary>Left trackpad click.</summary>
    LeftPadClick = 1 << 23,

    /// <summary>Right trackpad click.</summary>
    RightPadClick = 1 << 24,

    /// <summary>Dedicated quick-access button.</summary>
    QuickAccess = 1 << 25
}

/// <summary>
///     One complete sample of the physical controller, normalized by its native backend.
/// </summary>
/// <remarks>
///     Full state rather than deltas: a dropped delta leaves a control stuck forever, while a dropped
///     full state is corrected by the next one. Axes are normalized so no consumer needs to know the
///     device's raw ranges, centres or inversions. The native decoder owns that translation.
///     <para>
///         The canonical surface covers the controls represented by common virtual targets.
///         Missing physical controls remain unset; consumers need no vendor report layout.
///     </para>
///     <para>
///         A backend reports only what its hardware has and leaves the rest alone; a target renders only what
///         it can represent, dropping the rest rather than remapping it. Neither side invents a control.
///     </para>
/// </remarks>
public readonly record struct CanonicalControllerSample
{
    /// <summary>High-resolution timestamp of the sample, in UTC.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Buttons currently held.</summary>
    public CanonicalButtons Buttons { get; init; }

    /// <summary>Left stick X, from -1 to 1.</summary>
    public float LeftStickX { get; init; }

    /// <summary>Left stick Y, from -1 to 1, positive up.</summary>
    public float LeftStickY { get; init; }

    /// <summary>Right stick X, from -1 to 1.</summary>
    public float RightStickX { get; init; }

    /// <summary>Right stick Y, from -1 to 1, positive up.</summary>
    public float RightStickY { get; init; }

    /// <summary>Left trigger, from 0 to 1.</summary>
    public float LeftTrigger { get; init; }

    /// <summary>Right trigger, from 0 to 1.</summary>
    public float RightTrigger { get; init; }

    /// <summary>Left touch contact position on the horizontal axis, from -1 to 1.</summary>
    /// <remarks>
    ///     The touch surface is expressed as two independent contacts, left and right, because that
    ///     covers both shapes WSGM's virtual targets present: the Steam Deck's two separate trackpads
    ///     map one contact each, and the DualShock 4's single two-finger touchpad maps its first finger
    ///     to the left contact and its second to the right. A device with neither leaves these zero and
    ///     never reports a touch.
    /// </remarks>
    public float LeftPadX { get; init; }

    /// <summary>Left touch contact position on the vertical axis, from -1 to 1, positive up.</summary>
    public float LeftPadY { get; init; }

    /// <summary>Left touch contact pressure, from 0 to 1.</summary>
    public float LeftPadForce { get; init; }

    /// <summary>Right touch contact position on the horizontal axis, from -1 to 1.</summary>
    public float RightPadX { get; init; }

    /// <summary>Right touch contact position on the vertical axis, from -1 to 1, positive up.</summary>
    public float RightPadY { get; init; }

    /// <summary>Right touch contact pressure, from 0 to 1.</summary>
    public float RightPadForce { get; init; }

    /// <summary>Left stick capacitive contact strength, from 0 to 1.</summary>
    public float LeftStickForce { get; init; }

    /// <summary>Right stick capacitive contact strength, from 0 to 1.</summary>
    public float RightStickForce { get; init; }

    /// <summary>Motion, when the device has a sensor and it is available.</summary>
    public MotionSample? Motion { get; init; }

    /// <summary>
    ///     The neutral sample: nothing held, every axis centred.
    /// </summary>
    /// <remarks>
    ///     Published to the virtual target whenever forwarding stops â€” UI capture, target removal, game
    ///     exit, suspend, disconnect, plugin disable, or fault. Without it the last forwarded state stays
    ///     latched and the game keeps seeing a held control.
    /// </remarks>
    /// <param name="timestamp">Timestamp to stamp on the neutral sample.</param>
    /// <returns>A sample with every control at rest.</returns>
    public static CanonicalControllerSample Neutral(DateTimeOffset timestamp)
    {
        return new CanonicalControllerSample { Timestamp = timestamp };
    }
}

/// <summary>
///     One motion sample.
/// </summary>
/// <remarks>
///     Gyroscope and accelerometer are separate and optional because a device may have one without the
///     other, or its operating-system sensor stack may project only one of them. Synthesizing a missing
///     sensor would invent data.
/// </remarks>
public readonly record struct MotionSample
{
    /// <summary>Angular velocity around X, in degrees per second.</summary>
    public float GyroX { get; init; }

    /// <summary>Angular velocity around Y, in degrees per second.</summary>
    public float GyroY { get; init; }

    /// <summary>Angular velocity around Z, in degrees per second.</summary>
    public float GyroZ { get; init; }

    /// <summary>Whether the gyroscope values are present.</summary>
    public bool HasGyro { get; init; }

    /// <summary>Acceleration along X, in g.</summary>
    public float AccelX { get; init; }

    /// <summary>Acceleration along Y, in g.</summary>
    public float AccelY { get; init; }

    /// <summary>Acceleration along Z, in g.</summary>
    public float AccelZ { get; init; }

    /// <summary>Whether the accelerometer values are present.</summary>
    public bool HasAccelerometer { get; init; }

    /// <summary>Sensor timestamp, when the sensor supplies one distinct from the sample time.</summary>
    public DateTimeOffset? SensorTimestamp { get; init; }
}
