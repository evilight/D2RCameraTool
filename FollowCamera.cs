using System.Diagnostics;

namespace D2RCameraTool;

/// <summary>
/// Keeps the camera behind the character, derived from the character's own movement.
///
/// In Diablo the character always faces the direction it walks - there is no strafing and no
/// backpedalling - so the movement direction IS the facing, and the world position is locatable
/// at a fixed offset from the camera base (base-0x14 x, base-0x10 height, base-0x0c z), verified
/// across five sessions. A stored heading field exists but lives nowhere near the position, so
/// anchoring on it never worked reliably; this does not need it.
///
/// The camera SNAPS rather than interpolates, which is the important part. Rotating gradually
/// while the character walks is disorienting in a click-to-move game: the screen turns under the
/// player and the meaning of the next click changes while they are aiming it. Instead the
/// direction is measured from the first real step and the yaw is set to its final value in one
/// go, so the view is stable for the whole walk.
///
/// A new click that sends the character elsewhere changes the measured direction, which snaps
/// the camera again - once, at the start of that walk.
///
/// yaw = (+/-direction) + Offset. The sign and the offset are the only two unknowns, and both are
/// on the panel so they can be tuned while playing.
/// </summary>
internal sealed class FollowCamera
{
    /// <summary>
    /// World units that must accumulate before the direction is measured on a walk already in
    /// progress. One unit was far too short: the measured direction wandered by up to half a
    /// radian on a straight walk, and every wobble moved the camera. Three units is about a
    /// dozen frames, which averages the sampling noise out.
    /// </summary>
    private const float MoveWindow = 3.0f;

    /// <summary>
    /// The same, for the first measurement of a new walk, where being prompt matters more than
    /// being smooth - this is what makes the camera turn at the start of a walk rather than
    /// halfway through it.
    /// </summary>
    private const float StartWindow = 0.5f;

    /// <summary>
    /// How far the direction must turn before the camera is re-aimed mid-walk. Large, because a
    /// click that changes direction usually changes it a lot, and anything smaller is noise.
    /// </summary>
    private const double TurnThreshold = 0.5;

    /// <summary>Per-tick movement below this counts as standing still, in world units.</summary>
    private const float StillStep = 0.02f;

    /// <summary>How long without movement before the character counts as having stopped.</summary>
    private const double StillMs = 300;

    public bool Enabled;
    public bool Invert;

    /// <summary>Added to the measured direction to produce the yaw value. Tuned by trial.</summary>
    public double Offset;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private float _lastX, _lastZ;
    private float _accX, _accZ;
    private double _stillMs;
    private bool _stationary;
    private double _lastTickMs;
    private bool _haveLast;
    private double _direction;
    private bool _haveDirection;
    private bool _loggedRoll;
    private string _lastReason = "";

    public double Direction => _direction;
    public double LastYawWritten { get; private set; }
    public bool Active { get; private set; }
    public int SnapCount { get; private set; }

    /// <summary>Drops the tracked position, so a relocated camera does not produce a bogus jump.</summary>
    public void Reset()
    {
        _haveLast = false;
        _haveDirection = false;
        _accX = _accZ = 0;
        _stillMs = 0;
        _stationary = false;
        _loggedRoll = false;
        SnapCount = 0;
        Active = false;
    }

    /// <summary>
    /// Reads the character's world position, re-aims the camera when the direction actually
    /// changed, and holds it otherwise. Returns true when a value was written.
    /// </summary>
    public bool Tick()
    {
        if (!Enabled || !Program.ValuesFound || !Program.WriteAccessAvailable) return false;

        long baseAddr = Program.PitchValue.BaseAddress;
        if (baseAddr <= 0) return false;

        float? rx = Program.MemLib.ReadFloat((ulong)(baseAddr - 0x14));
        float? rz = Program.MemLib.ReadFloat((ulong)(baseAddr - 0x0C));
        if (rx is null || rz is null) return false;

        float x = rx.Value, z = rz.Value;

        double nowMs = _clock.Elapsed.TotalMilliseconds;
        double dt = nowMs - _lastTickMs;
        _lastTickMs = nowMs;
        if (dt <= 0) dt = 1;

        if (!_haveLast)
        {
            _lastX = x;
            _lastZ = z;
            _haveLast = true;
            return false;
        }

        // Per-tick step, for the standing-still test.
        float stepX = x - _lastX;
        float stepZ = z - _lastZ;
        float step = MathF.Sqrt(stepX * stepX + stepZ * stepZ);
        _lastX = x;
        _lastZ = z;

        bool wasStationary = _stationary;
        if (step > StillStep) _stillMs = 0; else _stillMs += dt;
        _stationary = _stillMs > StillMs;

        // Everything since the last measurement, so a short window still yields a stable angle.
        _accX += stepX;
        _accZ += stepZ;
        float acc = MathF.Sqrt(_accX * _accX + _accZ * _accZ);

        // A new walk: be prompt, because the whole point is to turn the camera at the start of it.
        bool starting = wasStationary && step > StillStep;
        float window = starting ? StartWindow : MoveWindow;
        if (acc < window) return false;

        double measured = Math.Atan2(_accZ, _accX);
        _accX = 0;
        _accZ = 0;

        bool snap;
        string reason;

        if (!_haveDirection || starting)
        {
            snap = true;
            reason = _haveDirection ? "new walk" : "first fix";
        }
        else
        {
            double change = measured - _direction;
            while (change > Math.PI) change -= 2 * Math.PI;
            while (change < -Math.PI) change += 2 * Math.PI;

            // Mid-walk only a real turn moves the camera. The measured direction wanders slightly
            // even on a straight line, and following that wander is what made the view feel wrong.
            snap = Math.Abs(change) > TurnThreshold;
            reason = $"turn {change:F3}";
        }

        if (!snap) return false;

        _direction = measured;
        _lastReason = reason;

        double target = (_direction + Offset) * (Invert ? -1 : 1);
        target = WrapPi(target);

        if (!Program.TryChangeValue(Program.CamPanValue, (float)target))
        {
            Active = false;
            return false;
        }

        SnapCount++;

        // A follow camera should be level. The isometric view carries a deliberate 5.7 degree roll
        // (0.099046 rad) that reads as the ground tilting once the camera sits behind the
        // character, so it is held at zero while following. Re-asserted rather than written once
        // because the engine restores its own camera from time to time.
        if (Program.RollValue.BaseAddress > 0)
        {
            float roll = Program.RollValue.Read();
            if (Math.Abs(roll) > 0.0005f)
            {
                Program.TryChangeValue(Program.RollValue, 0f);
                if (!_loggedRoll)
                {
                    _loggedRoll = true;
                    Program.LogLine($"Follow: holding roll at 0 (was {roll:F6}; the isometric view's "
                                    + "5.7 degree tilt reads as the ground leaning once the camera "
                                    + "is behind the character)");
                }
            }
        }

        LastYawWritten = target;
        Active = true;

        float mirror = float.NaN;
        foreach (long m in Program.CameraMirrors)
        {
            var v = Program.MemLib.ReadFloat((ulong)(m + 0x8));
            if (v.HasValue) { mirror = v.Value; break; }
        }

        Program.LogLine(
            $"Follow snap #{SnapCount} ({reason}): pos=({x:F2},{z:F2}) dir={_direction:F4} "
            + $"-> yaw={target:F4} mirror={mirror:F4} lag={WrapPi(mirror - target):F4}");

        return true;
    }

    private static double WrapPi(double radians)
    {
        const double twoPi = Math.PI * 2;
        while (radians > Math.PI) radians -= twoPi;
        while (radians <= -Math.PI) radians += twoPi;
        return radians;
    }
}
