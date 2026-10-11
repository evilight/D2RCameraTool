using System.Diagnostics;

namespace D2RCameraTool;

/// <summary>
/// WASD camera control: hold A/D to swing the view direction, W/S to tilt between ground
/// and sky.
///
/// Design notes, in the order the problems actually showed up:
///
///  * Movement is TIME-BASED (radians per second) rather than per tick, so the feel does
///    not change with frame rate or with how often the loop happens to run.
///  * Key state comes from TWO sources, OR-ed: GetAsyncKeyState and the keyboard hook's own
///    events. Polling alone is not enough, because the hook swallows W/A/S/D and a swallowed
///    key may never reach the input queue the polling API reads.
///  * Movement only applies while the game owns the foreground window, so the keys cannot
///    silently rotate the camera while the user is typing elsewhere.
///  * The write rate matters. Observed in game: the view shifted and then snapped back, i.e.
///    the engine re-asserts its own camera value every frame and we are racing it. Writing
///    more often wins more frames, so the update period is configurable down to 1 ms and the
///    caller must drive Tick from a timer that is NOT limited to the WM_TIMER granularity.
///  * Tick deliberately does NOT touch any control: it is called from a timer thread. It
///    publishes the values it wrote and the UI reads them at its own (throttled) pace.
/// </summary>
internal sealed class WasdCameraControl
{
    /// <summary>Radians per second at the default speed setting.</summary>
    public const double DefaultSpeed = 1.2;

    /// <summary>
    /// Camera update period in milliseconds. Measured in game: 1 ms is the smoothest, 8 ms
    /// visibly worse, 16 ms worse still. The step per write is speed * interval, so a shorter
    /// interval means a smaller step; that is what removes the judder, not responsiveness.
    /// </summary>
    public const int DefaultUpdateIntervalMs = 1;
    public const int MinUpdateIntervalMs = 1;
    public const int MaxUpdateIntervalMs = 100;

    // Limits are written in DEGREES and converted, so they can be reasoned about without
    // doing radians in your head. Shared by the WASD keys, the mouse-drag path and the spin
    // box ranges.
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

    /// <summary>Only act while the game owns the foreground window.</summary>
    public bool RequireGameFocus = true;

    /// <summary>Camera update period in milliseconds.</summary>
    public int UpdateIntervalMs = DefaultUpdateIntervalMs;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSample;
    private int _gamePid;

    /// <summary>
    /// Keys held according to the keyboard hook's own events. The hook sees a key whether or
    /// not it swallows it, unlike the polling API.
    /// </summary>
    private readonly HashSet<Keys> _hookHeld = new();

    /// <summary>Last raw state from each source, for the diagnostic line.</summary>
    private readonly Dictionary<Keys, (bool async, bool hook)> _lastSource = new();

    // Written from the timer thread, read from the hook callback on the UI thread.
    private volatile bool _gameFocused;

    /// <summary>
    /// Last value this control wrote, and how many times the game had replaced it by the time
    /// of the next write. Measured against D2R 3.0.91636 this stays at 0 even at a 1 ms
    /// interval, i.e. the engine does NOT re-assert this field every frame. Kept because it
    /// is the direct test for that, and because it would catch the behaviour returning.
    /// </summary>
    private float _lastYawWritten = float.NaN;
    private float _lastPitchWritten = float.NaN;
    public int YawRewrites { get; private set; }
    public int PitchRewrites { get; private set; }

    // Actual achieved tick rate. A requested interval is a request: Windows timer resolution
    // can silently floor it, so the rate is measured rather than assumed.
    private int _rateTicks;
    private double _rateWindowStartMs;
    public double MeasuredHz { get; private set; }

    // Post-release revert watch. How long to keep checking after the last write.
    private const double RevertWatchMs = 4000;
    private bool _watchArmed;
    private double _watchStartMs;
    private float _watchYaw;
    private bool _watchReported;

    public bool GameFocused => _gameFocused;
    public bool AnyKeyHeld { get; private set; }

    public bool RawA { get; private set; }
    public bool RawD { get; private set; }
    public bool RawW { get; private set; }
    public bool RawS { get; private set; }
    public bool RawAny => RawA || RawD || RawW || RawS;
    public int ForegroundPid { get; private set; }

    /// <summary>Latest yaw/pitch written to the game, for the UI to display when it is due.</summary>
    public float DisplayYaw { get; private set; }
    public float DisplayPitch { get; private set; }

    // Separate flags: if only yaw has ever been written, showing DisplayPitch would wrongly
    // reset the pitch readout to zero.
    public bool DisplayYawDirty { get; private set; }
    public bool DisplayPitchDirty { get; private set; }

    public void ClearDisplayDirty()
    {
        DisplayYawDirty = false;
        DisplayPitchDirty = false;
    }

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

    public static bool IsWasdKey(Keys key) => key is Keys.A or Keys.D or Keys.W or Keys.S;

    /// <summary>Whether the keyboard hook should swallow this key right now.</summary>
    public bool ShouldSuppress(Keys key) =>
        Enabled && BlockKeys && _gameFocused && IsWasdKey(key);

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
        + $"intervalMs={UpdateIntervalMs} measuredHz={MeasuredHz:F0} fineTimer={TimerResolution.IsRaised} "
        + $"attachedPid={_gamePid} foregroundPid={ForegroundPid} focused={_gameFocused} "
        + $"keys[A={SourceReport(Keys.A)} D={SourceReport(Keys.D)} "
        + $"W={SourceReport(Keys.W)} S={SourceReport(Keys.S)}] moved={AnyKeyHeld} "
        + $"engineRewrites[yaw={YawRewrites} pitch={PitchRewrites}] "
        + $"yawAddr=0x{Program.CamPanValue?.BaseAddress:X} pitchAddr=0x{Program.PitchValue?.BaseAddress:X}";

    /// <summary>
    /// Advances the camera by one step. Called from a timer thread; never touches a control.
    /// Returns true when a value was written.
    /// </summary>
    public bool Tick(int gameProcessId)
    {
        TimeSpan now = _clock.Elapsed;
        double dt = (now - _lastSample).TotalSeconds;
        _lastSample = now;

        // If the loop stalls (a modal dialog, a long scan) do not replay the gap as one huge
        // jump.
        if (dt > 0.1) dt = 0.1;
        if (dt <= 0) return false;

        // Measure the rate actually achieved, so a requested interval that Windows quietly
        // floors shows up in the log instead of being taken on trust.
        _rateTicks++;
        double nowMs = now.TotalMilliseconds;
        if (_rateWindowStartMs == 0) _rateWindowStartMs = nowMs;
        else if (nowMs - _rateWindowStartMs >= 1000) {
            MeasuredHz = _rateTicks * 1000.0 / (nowMs - _rateWindowStartMs);
            _rateTicks = 0;
            _rateWindowStartMs = nowMs;
        }

        _gamePid = gameProcessId;
        ForegroundPid = NativeInput.ForegroundProcessId();
        _gameFocused = gameProcessId > 0 && ForegroundPid == gameProcessId;

        // Sample the physical keys before any gate, so diagnostics can distinguish a missing
        // key from a blocked one.
        RawA = Down(Keys.A);
        RawD = Down(Keys.D);
        RawW = Down(Keys.W);
        RawS = Down(Keys.S);

        double yawStep = 0, pitchStep = 0;
        if (Enabled && (!RequireGameFocus || _gameFocused))
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
        if (!AnyKeyHeld)
        {
            WatchForRevert();
            return false;
        }

        // Without write access the attempt would fail every tick; the status line already says
        // the attach is read-only, so do not spam it from this thread.
        if (!Program.WriteAccessAvailable) return false;

        bool wrote = false;

        if (yawStep != 0 && Program.CamPanValue.BaseAddress > 0)
        {
            float cur = Program.CamPanValue.Read();
            if (!float.IsNaN(_lastYawWritten) && Math.Abs(cur - _lastYawWritten) > 1e-6f)
                YawRewrites++;   // something put its own value back

            float next = (float)Math.Clamp(cur + yawStep, YawMin, YawMax);
            if (Program.TryChangeValue(Program.CamPanValue, next))
            {
                WriteMirrors(0x8, next);
                _lastYawWritten = next;
                DisplayYaw = next;
                DisplayYawDirty = true;
                wrote = true;
            }
        }

        if (pitchStep != 0 && Program.PitchValue.BaseAddress > 0)
        {
            float cur = Program.PitchValue.Read();
            if (!float.IsNaN(_lastPitchWritten) && Math.Abs(cur - _lastPitchWritten) > 1e-6f)
                PitchRewrites++;

            float next = (float)Math.Clamp(cur + pitchStep, PitchMin, PitchMax);
            if (Program.TryChangeValue(Program.PitchValue, next))
            {
                WriteMirrors(0x0, next);
                _lastPitchWritten = next;
                DisplayPitch = next;
                DisplayPitchDirty = true;
                wrote = true;
            }
        }

        // Movement is running, so the post-release watch starts fresh from the next release.
        _watchArmed = false;
        return wrote;
    }

    /// <summary>
    /// Mirrors a value into every byte-exact camera copy. The camera lives at more than one
    /// address (originally observed 0x170 apart); leaving a stale copy behind gives the engine
    /// something to restore from.
    /// </summary>
    private static void WriteMirrors(long fieldOffset, float value)
    {
        long best = Program.PitchValue.BaseAddress;
        foreach (long m in Program.CameraMirrors)
        {
            if (m == best) continue;
            Program.MemLib.WriteFloat((ulong)(m + fieldOffset), value);
        }
    }

    /// <summary>
    /// Watches for the yaw value being restored after the keys are released.
    ///
    /// This is the case the in-hold counter could not see: it only ran while a key was held,
    /// which is exactly when the engine is NOT restoring. The reported symptom - the view
    /// follows while held and returns to its old position on release - is what this detects.
    /// </summary>
    private void WatchForRevert()
    {
        if (float.IsNaN(_lastYawWritten) || Program.CamPanValue.BaseAddress <= 0) return;

        double nowMs = _clock.Elapsed.TotalMilliseconds;

        if (!_watchArmed)
        {
            _watchArmed = true;
            _watchStartMs = nowMs;
            _watchYaw = _lastYawWritten;
            _watchReported = false;
            return;
        }

        if (_watchReported || nowMs - _watchStartMs > RevertWatchMs) return;

        float cur = Program.CamPanValue.Read();
        if (Math.Abs(cur - _watchYaw) <= 1e-6f) return;

        _watchReported = true;
        Program.LogLine(
            $"WASD yaw REVERTED {nowMs - _watchStartMs:F0} ms after the last write: "
            + $"we left {_watchYaw:F6}, memory now {cur:F6}. "
            + "The engine rebuilt the camera, so the value we write is restored rather than kept.");
    }
}
