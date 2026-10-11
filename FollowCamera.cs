using System.Diagnostics;

namespace D2RCameraTool;

/// <summary>
/// Keeps the camera behind the character, derived from the character's own movement.
///
/// A stored heading was hunted for several sessions and was found - a unit 2D direction vector
/// that reverses by exactly 180 degrees when the character turns - but it lives nowhere near the
/// world position, so it cannot be located reliably from the camera struct. Twice it fell inside
/// a dumped region, and once it did not. Anchoring on it was the wrong approach.
///
/// It is also unnecessary. In Diablo the character always faces the direction it walks: there is
/// no strafing and no backpedalling. So the movement direction IS the facing, and the world
/// position is locatable at a fixed offset from the camera base, verified across four sessions.
///
/// What remains is the mapping from a world direction to the yaw value, and that is affine:
/// yaw = +/-psi + offset. Two parameters, both settable from the UI, which is why this class
/// exposes Invert and Offset rather than trying to derive them.
/// </summary>
internal sealed class FollowCamera
{
    /// <summary>How far the character must move before the direction is updated, in world units.</summary>
    private const float MoveThreshold = 0.35f;

    /// <summary>How much of each new measurement to blend in. Low, because the facing should not jitter.</summary>
    private const float Smoothing = 0.25f;

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

    public double Direction => _direction;
    public double LastYawWritten { get; private set; }
    public bool Active { get; private set; }

    /// <summary>Drops the tracked position, so a relocated camera does not produce a bogus jump.</summary>
    public void Reset()
    {
        _haveLast = false;
        _haveDirection = false;
        Active = false;
    }

    /// <summary>
    /// Reads the character's world position, updates the movement direction if it moved enough,
    /// and writes the yaw. Returns true when a value was written.
    /// </summary>
    public bool Tick()
    {
        if (!Enabled || !Program.ValuesFound || !Program.WriteAccessAvailable) return false;

        // Offsets verified against the live game: the character's world position sits at
        // base-0x14 (x), base-0x10 (height), base-0x0c (z), just before the camera angles.
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

        if (moved >= MoveThreshold)
        {
            double measured = Math.Atan2(dz, dx);

            if (!_haveDirection)
            {
                _direction = measured;
                _haveDirection = true;
            }
            else
            {
                // Blend in the short way round, so crossing the +/-pi seam does not spin the camera.
                double delta = measured - _direction;
                while (delta > Math.PI) delta -= 2 * Math.PI;
                while (delta < -Math.PI) delta += 2 * Math.PI;
                _direction += delta * Smoothing;
            }

            _lastX = x;
            _lastZ = z;
        }

        if (!_haveDirection) return false;

        double target = (_direction + Offset) * (Invert ? -1 : 1);
        target = WrapPi(target);

        if (!Program.TryChangeValue(Program.CamPanValue, (float)target))
        {
            Active = false;
            return false;
        }

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
