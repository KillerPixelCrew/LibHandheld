using System.Numerics;

namespace LibHandheld.Internal.Input;

// Shared by every handheld package. The rest-gate thresholds were measured on the MSI Claw's LSM6DSO; a
// device that never meets them is simply left uncorrected.
/// <summary>Subtracts this IMU's measured zero-rate offset without absorbing aiming motion.</summary>
/// <remarks>
///     <para>
///         Correction is plain subtraction: no deadband and no zero-hold, so sensor noise stays continuous
///         and every rate a target integrates is the rate the die reported. The offset is measured from
///         rest windows recognized by three device-derived gates, whose thresholds come from stationary
///         captures taken on the reference unit — see "This part's gyroscope has a zero-rate offset" in the
///         plugin README for the measured numbers.
///     </para>
///     <para>
///         A steady yaw is the one motion no acceleration gate can distinguish from rest, so a device that
///         starts up already turning slowly — on a train or in a car — can measure that turn as its offset.
///         That is unavoidable without an external heading reference, so the design makes it survivable
///         instead: the magnitude limit bounds how wrong the value can be, and a run of agreeing windows
///         re-acquires. A single clamp on refinement would instead freeze the wrong value for the whole
///         device cycle, because the honest windows that follow are exactly the ones a clamp rejects.
///     </para>
/// </remarks>
internal sealed class StationaryGyroBiasCalibrator
{
    /// <summary>Reports per rest window: about two seconds at the gyrometer's 100 Hz cadence.</summary>
    public const int WindowSampleCount = 200;

    /// <summary>
    ///     Per-axis peak-to-peak angular rate a rest window may span. The noisiest axis spans up to
    ///     1.47 degrees/second across 200 stationary reports, so this admits every real rest window
    ///     while a hand's changing rate breaks the window immediately.
    /// </summary>
    private const float MaximumAxisSpan = 2f;

    /// <summary>
    ///     Per-axis peak-to-peak acceleration a rest window may span, in g. Stationary reports span at
    ///     most 0.023 g; 0.05 g still detects roughly 1.4 degrees/second of pitch or roll, which is
    ///     what makes a slowly tilted device fail the gate instead of teaching a false offset.
    /// </summary>
    private const float MaximumAccelerationSpan = 0.05f;

    /// <summary>The narrowest gravity magnitude, in g, that a rest window's acceleration may show.</summary>
    private const float MinimumGravityMagnitude = 0.85f;

    /// <summary>The widest gravity magnitude, in g, that a rest window's acceleration may show.</summary>
    private const float MaximumGravityMagnitude = 1.15f;

    /// <summary>
    ///     The largest offset magnitude accepted as hardware, in degrees/second. This part's measured
    ///     offset is under 1; anything far above it is a sustained rotation, not a zero-rate error.
    /// </summary>
    private const float MaximumBiasMagnitude = 5f;

    /// <summary>How far, per axis, a rest window may sit from the measured offset and still refine it.</summary>
    private const float MaximumRefinementDelta = 0.5f;

    /// <summary>The fraction of an accepted refinement applied, damping a contaminated window.</summary>
    public const float RefinementWeight = 0.25f;

    /// <summary>
    ///     Consecutive rest windows that agree with each other but not with the measured offset before
    ///     that offset is replaced outright. One distant window is contamination; a run of them means
    ///     the offset was measured during motion, or the part genuinely drifted past refinement range.
    /// </summary>
    public const int ReacquireWindowCount = 3;

    private Vector3 _accelerationMaximum;
    private Vector3 _accelerationMinimum;
    private Vector3 _angularMaximum;
    private Vector3 _angularMinimum;
    private Vector3 _angularSum;

    private int _count;
    private Vector3? _distantCandidate;
    private int _distantCount;

    /// <summary>The zero-rate offset measured so far in this device cycle, in degrees/second.</summary>
    /// <remarks>Null until the first rest window completes; corrections pass through until then.</remarks>
    public Vector3? Bias { get; private set; }

    /// <summary>Forgets the measured offset and any window in progress, at a cycle boundary.</summary>
    public void Reset()
    {
        ResetWindow();
        Bias = null;
        _distantCandidate = null;
        _distantCount = 0;
    }

    /// <summary>Observes one report and returns its corrected angular velocity.</summary>
    /// <param name="angularVelocity">Sensor-space angular velocity in degrees/second.</param>
    /// <param name="acceleration">The same report's acceleration in g, used only to detect rest.</param>
    /// <returns>
    ///     The angular velocity less the measured offset, or unchanged while no offset is known. A
    ///     caller receiving an uncorrected value is being told honestly that rest has not occurred.
    /// </returns>
    public Vector3 Correct(Vector3 angularVelocity, Vector3 acceleration)
    {
        Observe(angularVelocity, acceleration);
        return Bias is { } bias ? angularVelocity - bias : angularVelocity;
    }

    private void Observe(Vector3 angularVelocity, Vector3 acceleration)
    {
        var gravity = acceleration.Length();
        if (!float.IsFinite(gravity)
            || !float.IsFinite(angularVelocity.LengthSquared())
            || gravity < MinimumGravityMagnitude
            || gravity > MaximumGravityMagnitude)
        {
            ResetWindow();
            return;
        }

        Accumulate(angularVelocity, acceleration);
        if (Exceeds(_angularMaximum - _angularMinimum, MaximumAxisSpan)
            || Exceeds(_accelerationMaximum - _accelerationMinimum, MaximumAccelerationSpan))
        {
            // The device moved during this window. Restart from the current report rather than
            // discarding it, so a window can begin the moment motion stops.
            ResetWindow();
            Accumulate(angularVelocity, acceleration);
            return;
        }

        if (_count < WindowSampleCount)
        {
            return;
        }

        var candidate = _angularSum / _count;
        ResetWindow();
        if (candidate.Length() > MaximumBiasMagnitude)
        {
            return;
        }

        if (Bias is not { } bias)
        {
            Adopt(candidate);
            return;
        }

        var delta = candidate - bias;
        if (Exceeds(Vector3.Abs(delta), MaximumRefinementDelta))
        {
            // Too far to be a refinement. Rather than clamp — which would freeze a first offset
            // measured during a slow turn for the whole device cycle, because every honest window
            // afterwards is exactly this far away — require a run of windows that agree with each
            // other, then take the newest outright.
            _distantCount = _distantCandidate is { } previous
                            && !Exceeds(Vector3.Abs(candidate - previous), MaximumRefinementDelta)
                ? _distantCount + 1
                : 1;
            _distantCandidate = candidate;
            if (_distantCount >= ReacquireWindowCount)
            {
                Adopt(candidate);
            }

            return;
        }

        Bias = bias + delta * RefinementWeight;
        _distantCandidate = null;
        _distantCount = 0;
    }

    private void Adopt(Vector3 candidate)
    {
        Bias = candidate;
        _distantCandidate = null;
        _distantCount = 0;
    }

    private static bool Exceeds(Vector3 value, float limit)
    {
        return value.X > limit || value.Y > limit || value.Z > limit;
    }

    private void Accumulate(Vector3 angularVelocity, Vector3 acceleration)
    {
        if (_count == 0)
        {
            _angularMinimum = angularVelocity;
            _angularMaximum = angularVelocity;
            _accelerationMinimum = acceleration;
            _accelerationMaximum = acceleration;
        }
        else
        {
            _angularMinimum = Vector3.Min(_angularMinimum, angularVelocity);
            _angularMaximum = Vector3.Max(_angularMaximum, angularVelocity);
            _accelerationMinimum = Vector3.Min(_accelerationMinimum, acceleration);
            _accelerationMaximum = Vector3.Max(_accelerationMaximum, acceleration);
        }

        _count++;
        _angularSum += angularVelocity;
    }

    private void ResetWindow()
    {
        _count = 0;
        _angularSum = default;
        _angularMinimum = default;
        _angularMaximum = default;
        _accelerationMinimum = default;
        _accelerationMaximum = default;
    }
}

/// <summary>
///     Area-preserving resampler from the gyrometer's 100 Hz cadence onto the controller frames.
/// </summary>
/// <remarks>
///     Attaching raw readings to ~125 Hz frames makes some frames repeat a stale value and others jump
///     two sensor periods, in a repeating 40 ms beat that integrates as jagged angular steps. Each
///     frame instead reports the average angular velocity over exactly the interval since the previous
///     frame, computed from the zero-order-held sensor integral: the total rotation Steam integrates
///     stays exact, the beat disappears, and no latency is added. A reading older than
///     the quiet interval stops contributing, so the average decays to zero
///     on a quiet (still) sensor rather than replaying the last angular velocity forever.
/// </remarks>
internal sealed class GyroFrameResampler
{
    private readonly Lock _gate = new();
    private readonly TimeSpan _quietAfter;
    private DateTimeOffset? _accountedTo;
    private DateTimeOffset? _lastFrame;
    private Vector3 _omega;
    private Vector3 _pendingDegrees;
    private DateTimeOffset _quietCap;

    /// <summary>Creates a resampler.</summary>
    /// <param name="quietAfter">
    ///     How long a reading keeps contributing without a newer one, after which the average decays to
    ///     zero rather than replaying a stale rate.
    /// </param>
    public GyroFrameResampler(TimeSpan quietAfter)
    {
        _quietAfter = quietAfter;
    }

    /// <summary>Clears all integration state at a device-cycle boundary.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _omega = default;
            _quietCap = default;
            _accountedTo = null;
            _pendingDegrees = default;
            _lastFrame = null;
        }
    }

    /// <summary>Feeds one sensor reading, accumulating the angle the previous one covered.</summary>
    /// <param name="omegaDegreesPerSecond">Angular velocity in the published basis.</param>
    /// <param name="stamp">The reading's sensor timestamp.</param>
    public void OnReading(Vector3 omegaDegreesPerSecond, DateTimeOffset stamp)
    {
        lock (_gate)
        {
            AdvanceUnderGate(stamp);
            _omega = omegaDegreesPerSecond;
            _quietCap = stamp + _quietAfter;
        }
    }

    /// <summary>Returns the average angular velocity since the previous frame.</summary>
    /// <param name="now">The controller frame's timestamp.</param>
    /// <returns>Degrees per second whose integral over the frame equals the sensor's.</returns>
    public Vector3 FrameAverage(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceUnderGate(now);
            if (_lastFrame is not { } previous || now <= previous)
            {
                // First frame, or a non-advancing frame clock: the held reading is the only
                // defensible answer, and pending angle stays banked for the next real frame.
                _lastFrame ??= now;
                return _omega;
            }

            var average = _pendingDegrees / (float)(now - previous).TotalSeconds;
            _pendingDegrees = Vector3.Zero;
            _lastFrame = now;
            return average;
        }
    }

    private void AdvanceUnderGate(DateTimeOffset to)
    {
        if (_accountedTo is not { } from)
        {
            _accountedTo = to;
            return;
        }

        if (to <= from)
        {
            return;
        }

        // The held velocity covers time only up to the quiet cap; beyond it the sensor's silence
        // means stillness and the integral stops growing.
        var covered = to < _quietCap ? to : _quietCap;
        if (covered > from)
        {
            _pendingDegrees += _omega * (float)(covered - from).TotalSeconds;
        }

        _accountedTo = to;
    }
}
