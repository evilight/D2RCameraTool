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
    /// World units that must accumulate before the direction is re-measured. A step, not a
    /// twitch: below this the delta is mostly float noise and would jitter the camera.
    /// </summary>
    private const float MoveThreshold = 1.0f;

    /// <summary>
    /// How much the direction must change before the camera is re-aimed. Anything smaller is
    /// held, so the camera sits perfectly still while the character walks a straight line.
    /// </summary>
    private const double SnapThreshold = 0.08;

    public bool Enabled;
    public bool Invert;

    /// <summary>Added to the measured direction to produce the yaw value. Tuned by trial.</summary>
    public double Offset;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private float _lastX, _lastZ;
    private bool _haveLast;
    private double _direction;
    private bool _haveDirection;
    private bool _loggedRoll;
    private DateTime _lastLog = DateTime.MinValue;

    public double Direction => _direction;
    public double LastYawWritten { get; private set; }
    public bool Active { get; private set; }
    public int SnapCount { get; private set; }

    /// <summary>Drops the tracked position, so a relocated camera does not produce a bogus jump.</summary>
    public void Reset()
    {
        _haveLast = false;
        _haveDirection = false;
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

        if (!_haveLast)
        {
            _lastX = x;
            _lastZ = z;
            _haveLast = true;
            return false;
        }

        float dx = x - _lastX;
        float dz = z - _lastZ;
        float moved = MathF.Sqrt(dx * dx + dz * dz);

        if (moved < MoveThreshold) return false;   // hold: too little movement to mean anything

        _lastX = x;
        _lastZ = z;

        double measured = Math.Atan2(dz, dx);
        bool snap;

        if (!_haveDirection)
        {
            _direction = measured;
            _haveDirection = true;
            snap = true;
        }
        else
        {
            double change = measured - _direction;
            while (change > Math.PI) change -= 2 * Math.PI;
            while (change < -Math.PI) change += 2 * Math.PI;

            // Only a real change of direction moves the camera - a new click. Anything under the
            // threshold is measurement noise on a straight walk and must not nudge the view.
            if (Math.Abs(change) < SnapThreshold) return false;

            _direction = measured;
            snap = true;
        }

        if (!snap) return false;

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
            $"Follow snap #{SnapCount}: pos=({x:F2},{z:F2}) dir={_direction:F4} "
            + $"-> yaw={target:F4} mirror={mirror:F4} diff={mirror - target:F4}");

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
