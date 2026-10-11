using System.Diagnostics;

namespace D2RCameraTool;

/// <summary>
/// WASD camera control: hold A/D to swing the view direction, W/S to tilt between ground
/// and sky.
///
/// Two deliberate choices:
///
///  * movement is TIME-BASED (radians per second) rather than per tick, so the feel does
///    not change with frame rate or with how often the idle loop happens to run;
///  * movement only applies while the game owns the foreground window, so the keys cannot
///    silently rotate the camera while the user is typing somewhere else. The same test
///    drives optional key blocking, so the game never sees a key we consumed.
/// </summary>
internal sealed class WasdCameraControl
{
    /// <summary>Radians per second at the default speed setting.</summary>
    public const double DefaultSpeed = 1.2;

    // Yaw wraps, so it can rotate forever. Pitch is clamped to a range that keeps the camera
    // above the ground: -1.55 rad is almost straight down, +0.30 lifts it to look slightly up.
    // These are the only two numbers to tune if the range feels wrong in game.
    public const double PitchMin = -1.55;
    public const double PitchMax = 0.30;

    public bool Enabled = true;
    public double SpeedRadPerSec = DefaultSpeed;
    public bool InvertYaw;
    public bool InvertPitch;

    /// <summary>Swallow W/A/S/D before the game sees them, while the game is focused.</summary>
    public bool BlockKeys = true;

    /// <summary>Only act while the game owns the foreground window.</summary>
    public bool RequireGameFocus = true;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSample;

    public bool GameFocused { get; private set; }
    public bool AnyKeyHeld { get; private set; }

    /// <summary>Keys this control owns.</summary>
    public static bool IsWasdKey(Keys key) =>
        key is Keys.A or Keys.D or Keys.W or Keys.S;

    /// <summary>Whether the keyboard hook should swallow this key right now.</summary>
    public bool ShouldSuppress(Keys key) =>
        Enabled && BlockKeys && GameFocused && IsWasdKey(key);

    /// <summary>
    /// Advances the camera by one frame. Returns true when a value was written, so the
    /// caller can refresh the UI only when something actually changed.
    /// </summary>
    public bool Tick(int gameProcessId)
    {
        TimeSpan now = _clock.Elapsed;
        double dt = (now - _lastSample).TotalSeconds;
        _lastSample = now;

        // If the idle loop stalls (a modal dialog, a long scan) do not replay the gap as one
        // huge jump.
        if (dt > 0.1) dt = 0.1;
        if (dt <= 0) return false;

        GameFocused = gameProcessId > 0 && NativeInput.ForegroundProcessId() == gameProcessId;

        double yawStep = 0, pitchStep = 0;
        if (Enabled && (!RequireGameFocus || GameFocused))
        {
            double step = SpeedRadPerSec * dt;
            if (NativeInput.IsDown(Keys.A)) yawStep -= step;
            if (NativeInput.IsDown(Keys.D)) yawStep += step;
            if (NativeInput.IsDown(Keys.W)) pitchStep -= step;   // W: look toward the ground
            if (NativeInput.IsDown(Keys.S)) pitchStep += step;   // S: look toward the sky
            if (InvertYaw) yawStep = -yawStep;
            if (InvertPitch) pitchStep = -pitchStep;
        }

        AnyKeyHeld = yawStep != 0 || pitchStep != 0;
        if (!AnyKeyHeld) return false;

        bool wrote = false;

        if (yawStep != 0 && Program.CamPanValue.BaseAddress > 0)
        {
            float next = WrapToPi(Program.CamPanValue.Read() + (float)yawStep);
            if (Program.TryChangeValue(Program.CamPanValue, next))
            {
                Program.MainFormInstance.SetYawDisplay(next);
                wrote = true;
            }
        }

        if (pitchStep != 0 && Program.PitchValue.BaseAddress > 0)
        {
            float next = (float)Math.Clamp(Program.PitchValue.Read() + pitchStep, PitchMin, PitchMax);
            if (Program.TryChangeValue(Program.PitchValue, next))
            {
                Program.MainFormInstance.SetPitchDisplay(next);
                wrote = true;
            }
        }

        return wrote;
    }

    /// <summary>Keeps yaw in (-pi, pi] so a long hold cannot drift into a huge float.</summary>
    private static float WrapToPi(float radians)
    {
        const float TwoPi = (float)(Math.PI * 2);
        while (radians > Math.PI) radians -= TwoPi;
        while (radians <= -Math.PI) radians += TwoPi;
        return radians;
    }
}
