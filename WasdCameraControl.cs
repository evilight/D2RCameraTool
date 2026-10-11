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

    /// <summary>Camera update period in milliseconds (~62 Hz).</summary>
    public const int DefaultUpdateIntervalMs = 16;

    // Limits are written in DEGREES and converted, so they can be reasoned about without
    // doing radians in your head. Shared by the WASD keys and the mouse-drag path.
    //
    // Yaw is CLAMPED rather than wrapped: wrapping made the value jump from +180 to -180,
    // which reads as the view snapping round. Clamping does not reduce the reachable range --
    // [-180, +180] spans a full circle, just without the discontinuity at the ends.
    //
    // Pitch is the camera's elevation. Negative looks down at the ground, positive lifts the
    // camera to look up. -89 is almost straight down; +89 puts the camera almost level with
    // the player looking up, which the game was never authored for and may render oddly.
    public const double YawMinDegrees = -180;
    public const double YawMaxDegrees = 180;
    public const double PitchMinDegrees = -89;
    public const double PitchMaxDegrees = 89;

    private const double DegToRad = Math.PI / 180.0;

    public const double YawMin = YawMinDegrees * DegToRad;
    public const double YawMax = YawMaxDegrees * DegToRad;
    public const double PitchMin = PitchMinDegrees * DegToRad;
    public const double PitchMax = PitchMaxDegrees * DegToRad;

    public bool Enabled = true;
    public double SpeedRadPerSec = DefaultSpeed;
    public bool InvertYaw;
    public bool InvertPitch;

    /// <summary>Swallow W/A/S/D before the game sees them, while the game is focused.</summary>
    public bool BlockKeys = true;

    /// <summary>
    /// How often the camera is updated, in milliseconds. Exposed because the write rate
    /// relative to the render rate is one of the candidate causes of ghosting while the
    /// camera moves, and it is the only lever the tool itself controls.
    /// </summary>
    public int UpdateIntervalMs = 16;

    /// <summary>Only act while the game owns the foreground window.</summary>
    public bool RequireGameFocus = true;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSample;
    private int _gamePid;

    /// <summary>
    /// Keys held according to the keyboard hook's own events.
    ///
    /// This exists because polling and swallowing can defeat each other: GetAsyncKeyState
    /// reads the state a key reaches once it is posted to the input queue, and a low-level
    /// hook that returns non-zero discards the key before that happens. With blocking on,
    /// the polling source can therefore report "up" for exactly the keys we are consuming.
    /// The hook sees the key regardless, so the two sources are OR-ed.
    /// </summary>
    private readonly HashSet<Keys> _hookHeld = new();

    /// <summary>Last raw state from each source, for the diagnostic line.</summary>
    private readonly Dictionary<Keys, (bool async, bool hook)> _lastSource = new();

    public bool GameFocused { get; private set; }
    public bool AnyKeyHeld { get; private set; }

    // Raw physical key state, sampled before any gating. Kept separate so a diagnostic can
    // tell "the key was never seen" apart from "the key was seen but a gate stopped it".
    public bool RawA { get; private set; }
    public bool RawD { get; private set; }
    public bool RawW { get; private set; }
    public bool RawS { get; private set; }
    public bool RawAny => RawA || RawD || RawW || RawS;
    public int ForegroundPid { get; private set; }

    public void OnKeyPressed(Keys key)
    {
        if (IsWasdKey(key)) _hookHeld.Add(key);
    }

    public void OnKeyReleased(Keys key)
    {
        _hookHeld.Remove(key);
    }

    /// <summary>
    /// Drops all hook-tracked keys. Called when the hook is re-installed: if it was removed
    /// while a key was held, the matching key-up never arrives and the direction would stick.
    /// </summary>
    public void ClearHeldKeys() => _hookHeld.Clear();

    private bool Down(Keys key)
    {
        bool asyncDown = NativeInput.IsDown(key);
        bool hookDown = _hookHeld.Contains(key);
        _lastSource[key] = (asyncDown, hookDown);
        return asyncDown || hookDown;
    }

    private string SourceReport(Keys key) =>
        _lastSource.TryGetValue(key, out var s)
            ? $"async={(s.async ? "Y" : "n")}/hook={(s.hook ? "Y" : "n")}"
            : "?";

    /// <summary>Every gate that can stop WASD from moving the camera, for the log.</summary>
    public string DescribeGates() =>
        $"enabled={Enabled} block={BlockKeys} requireFocus={RequireGameFocus} "
        + $"attachedPid={_gamePid} foregroundPid={ForegroundPid} focused={GameFocused} "
        + $"keys[A={SourceReport(Keys.A)} D={SourceReport(Keys.D)} "
        + $"W={SourceReport(Keys.W)} S={SourceReport(Keys.S)}] moved={AnyKeyHeld} "
        + $"yawAddr=0x{Program.CamPanValue?.BaseAddress:X} pitchAddr=0x{Program.PitchValue?.BaseAddress:X}";

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

        _gamePid = gameProcessId;
        ForegroundPid = NativeInput.ForegroundProcessId();
        GameFocused = gameProcessId > 0 && ForegroundPid == gameProcessId;

        // Sample the physical keys before any gate, so diagnostics can distinguish a missing
        // key from a blocked one. Two independent sources, OR-ed - see _hookHeld.
        RawA = Down(Keys.A);
        RawD = Down(Keys.D);
        RawW = Down(Keys.W);
        RawS = Down(Keys.S);

        double yawStep = 0, pitchStep = 0;
        if (Enabled && (!RequireGameFocus || GameFocused))
        {
            double step = SpeedRadPerSec * dt;
            if (RawA) yawStep -= step;
            if (RawD) yawStep += step;
            if (RawW) pitchStep -= step;   // W: look toward the ground
            if (RawS) pitchStep += step;   // S: look toward the sky
            if (InvertYaw) yawStep = -yawStep;
            if (InvertPitch) pitchStep = -pitchStep;
        }

        AnyKeyHeld = yawStep != 0 || pitchStep != 0;
        if (!AnyKeyHeld) return false;

        bool wrote = false;

        if (yawStep != 0 && Program.CamPanValue.BaseAddress > 0)
        {
            float next = (float)Math.Clamp(Program.CamPanValue.Read() + yawStep, YawMin, YawMax);
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
}
