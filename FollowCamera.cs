using System.Diagnostics;

namespace D2RCameraTool;

/// <summary>
/// Keeps the camera behind the character, but ONLY while the character is standing still.
///
/// The restriction is not a refinement, it is the whole design. Diablo converts the screen click
/// position into a world destination, so rotating the camera moves the world point under the
/// cursor - which moves the character's destination, which changes its direction, which changes
/// what this class measures and rotates to. That is a closed feedback loop, and the trace shows it
/// running: on one walk the measured direction went 0.276 -> 0.4877 -> 1.2123 -> 2.6935 -> 2.841
/// rad over about nine world units, a near reversal with no click involved, and the position steps
/// stayed at a constant 3.1 units throughout, so the character was not stopping - it was being
/// re-aimed by our own rotation.
///
/// Writing yaw only when the character is stationary breaks the loop at its weakest point: there
/// is no movement in flight to disturb, so the rotation cannot feed back into anything. The camera
/// is corrected once the character arrives, which also places it behind the facing they arrived
/// with.
///
/// The cost, which cannot be avoided: if the next click sends the character in a different
/// direction, that walk happens with the camera still aimed at the previous one, so it reads as
/// diagonal on screen until the character stops again.
///
/// In Diablo the character always faces the direction it walks - no strafing, no backpedalling -
/// so the movement direction IS the facing, and it is read from the world position at base-0x14
/// (x) and base-0x0c (z), an offset verified across five sessions. A stored heading field exists
/// but lives nowhere near the position, which is why it was abandoned.
/// </summary>
internal sealed class FollowCamera
{
    /// <summary>World units accumulated while moving before the direction is measured.</summary>
    private const float MoveWindow = 3.0f;

    /// <summary>Per-tick movement below this counts as standing still, in world units.</summary>
    private const float StillStep = 0.02f;

    /// <summary>How long without movement before the character counts as having stopped.</summary>
    private const double StillMs = 300;

    /// <summary>How much the walk direction must differ from the camera before it is corrected.</summary>
    private const double TurnThreshold = 0.2;

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

        // Accumulate the walk direction as the character moves. Measured over three units, which
        // averages out the sampling noise: at one unit the value wandered by half a radian.
        _accX += stepX;
        _accZ += stepZ;
        float acc = MathF.Sqrt(_accX * _accX + _accZ * _accZ);
        if (acc >= MoveWindow)
        {
            _direction = Math.Atan2(_accZ, _accX);
            _haveDirection = true;
            _accX = 0;
            _accZ = 0;
        }

        // Corrct ONLY once the character has come to rest. Any rotation while it is walking moves
        // the world point under the cursor, which moves the destination, which changes the
        // direction - the feedback loop this class exists to avoid.
        bool justStopped = !wasStationary && _stationary;
        if (!justStopped || !_haveDirection) return false;

        double target = (_direction + Offset) * (Invert ? -1 : 1);
        target = WrapPi(target);

        double change = target - Program.CamPanValue.Read();
        while (change > Math.PI) change -= 2 * Math.PI;
        while (change < -Math.PI) change += 2 * Math.PI;
        if (Math.Abs(change) < TurnThreshold) return false;

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
            $"Follow corrected on arrival #{SnapCount}: pos=({x:F2},{z:F2}) "
            + $"walked dir={_direction:F4} -> yaw={target:F4} (was {Program.CamPanValue.Read():F4})");

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
