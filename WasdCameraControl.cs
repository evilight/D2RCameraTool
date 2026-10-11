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
    /// <summary>
    /// Radians per second of yaw and pitch travel.
    ///
    /// Measured on the live game: the old value of 1.2 produced exactly 1.2000 rad/s, so the
    /// control loop was never at fault - it was simply too fast to aim with. 1.2 rad/s is 69
    /// degrees per second, a full turn in 5.2 seconds, which also means the +/-180 degree clamp is
    /// reached after five seconds of holding a key. 0.5 gives a full turn in about 12.6 seconds.
    /// </summary>
    public const double DefaultSpeed = 0.5;

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
    // which reads as the view snapping round.
    //
    // The limits were originally the full [-180, +180], which the camera cannot actually use.
    // Two separate things narrow the useful range:
    //
    //   Below about -30 degrees the view degrades. A yaw sweep shows the scene going dark from
    //   roughly -45 and staying dark to -180, with the ground reduced to a thin diagonal strip -
    //   the camera is at or below the terrain, because Diablo has no camera collision. The camera
    //   also turns at only half the rate of the yaw value, so what the value buys is half what it
    //   looks like.
    //
    //   Above about +30 degrees of pitch the view is looking at the horizon and past the edge of
    //   the level, and the ground is rendered edge-on. A pitch sweep shows the horizon appearing
    //   around +10 and the screen mostly void by +31.
    //
    // So: yaw [-30, +90] and pitch [-89, +30], which is the region where the view changes evenly
    // and stays inside the level.
    //
    // Pitch is the camera's elevation. Negative looks down at the ground, positive lifts the
    // camera to look up. -89 is almost straight down; +30 is already close to the horizon.
    public static double YawMinDegrees = -30;
    public static double YawMaxDegrees = 90;
    public static double PitchMinDegrees = -89;
    public static double PitchMaxDegrees = 30;

    private const double DegToRad = Math.PI / 180.0;

    // Properties, not constants: the limits are settable from settings.ini so they can be tuned
    // without a rebuild, and the spin box ranges follow them.
    public static double YawMin => YawMinDegrees * DegToRad;
    public static double YawMax => YawMaxDegrees * DegToRad;
    public static double PitchMin => PitchMinDegrees * DegToRad;
    public static double PitchMax => PitchMaxDegrees * DegToRad;

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

    /// <summary>
    /// Lift the camera when yaw is negative.
    ///
    /// The camera does not orbit on a flat circle. Watching a yaw sweep, the view goes dark from
    /// about -79 degrees onward and stays dark to -180, with the ground reduced to a thin diagonal
    /// strip - the camera is at or below the terrain, and Diablo has no camera collision to stop
    /// it. The same sweep is perfectly fine at +180. Since +180 and -180 would be the same camera
    /// position on any circular orbit, flat or tilted, the azimuth cannot be equal to the yaw
    /// value; the only reading that fits both the sinking and the two ends being maximally
    /// different is that the camera turns at half the rate of the yaw value, so the height follows
    /// sin(yaw / 2) - lowest at -180, highest at +180.
    ///
    /// Measured on the live game: at yaw = -180 the camera comes back above ground at height
    /// 0.489118, against a base of 0.239118. That is 0.25 of correction at the arc's lowest point,
    /// which is the gain below. Correction is applied only on the sinking side, so the positive
    /// half is left exactly as the game has it.
    /// </summary>
    public bool AutoHeight;
    public double AutoHeightGain = 0.25;

    /// <summary>The height the user asked for, before compensation.</summary>
    public double BaseHeight = DefaultHeight;

    public const double DefaultHeight = 0.239118;

    public double LastHeightCorrection { get; private set; }

    private double _lastHeightLogged = double.NaN;

    private double _lastRateLogMs;
    private float _rateRefYaw;
    private bool _rateRefValid;

    // Continuous camera monitor. Samples the value we manage and reports any change we did not
    // make, with the context. Unbounded on purpose - see MonitorCamera.
    private const double MonitorEveryMs = 250;
    private double _nextMonitorMs;
    private double _lastWriteMs;
    private float _lastSeenYaw = float.NaN;
    private int _externalChanges;

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

    /// <summary>
    /// Compact fingerprint of every gate plus the held keys. The caller logs only when this
    /// changes, so holding a key produces one line instead of one every throttle interval.
    /// </summary>
    public string Signature()
    {
        string keys = (RawA ? "A" : "") + (RawD ? "D" : "") + (RawW ? "W" : "") + (RawS ? "S" : "");
        return $"{(Enabled ? "on" : "off")}/{(BlockKeys ? "block" : "pass")}"
             + $"/{(RequireGameFocus ? "focus" : "any")}/f={(_gameFocused ? 1 : 0)}/k={keys}";
    }

    /// <summary>Every gate that can stop WASD from moving the camera, for the log.</summary>
    public string DescribeGates() =>
        $"enabled={Enabled} block={BlockKeys} requireFocus={RequireGameFocus} "
        + $"intervalMs={UpdateIntervalMs} measuredHz={MeasuredHz:F0} fineTimer={TimerResolution.IsRaised} "
        + $"attachedPid={_gamePid} foregroundPid={ForegroundPid} focused={_gameFocused} "
        + $"keys[A={SourceReport(Keys.A)} D={SourceReport(Keys.D)} "
        + $"W={SourceReport(Keys.W)} S={SourceReport(Keys.S)}] moved={AnyKeyHeld} "
        + $"engineRewrites[yaw={YawRewrites} pitch={PitchRewrites}] "
        + $"externalChanges={_externalChanges} "
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

        ApplyAutoHeight();

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
            MonitorCamera();
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
                _lastSeenYaw = next;                    // our own write, not an external change
                _lastWriteMs = _clock.Elapsed.TotalMilliseconds;
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

        // Throttled rate trace. SpeedRadPerSec is what we intend to write; this is what actually
        // reaches memory. Measuring the view from captured frames suggested the camera turns about
        // an order of magnitude slower than configured, and the frame measurement cannot be
        // trusted for the absolute angle - an isometric camera's rotation is a projective
        // transform on screen, so an affine fit underestimates it badly. This reads the value.
        double logNow = _clock.Elapsed.TotalMilliseconds;
        if (logNow - _lastRateLogMs > 500)
        {
            double dtSec = (logNow - _lastRateLogMs) / 1000.0;
            float yawNow = Program.CamPanValue.Read();
            if (_rateRefValid && dtSec > 0.05 && !float.IsNaN(yawNow))
            {
                double dRad = yawNow - _rateRefYaw;
                Program.LogLine(
                    $"WASD rate: yaw {_rateRefYaw:F6} -> {yawNow:F6} in {dtSec:F3}s = "
                    + $"{dRad / dtSec:F4} rad/s ({dRad * 180 / Math.PI / dtSec:F1} deg/s); "
                    + $"configured {SpeedRadPerSec:F3} rad/s = "
                    + $"{SpeedRadPerSec * 180 / Math.PI:F1} deg/s; "
                    + $"intervalMs={UpdateIntervalMs} measuredHz={MeasuredHz:F0} "
                    + $"yawRewrites={YawRewrites}");
            }
            _rateRefYaw = yawNow;
            _rateRefValid = !float.IsNaN(yawNow);
            _lastRateLogMs = logNow;
        }

        // Movement is running; the monitor keeps watching for anything we did not write.
        MonitorCamera();
        return wrote;
    }

    /// <summary>
    /// Applies the arc compensation to the camera height.
    ///
    /// Runs on every tick rather than only when a key is held, because the yaw can also be changed
    /// by the mouse drag, by a preset or by typing in the box, and the camera must not be left
    /// under the ground after any of them.
    /// </summary>
    private void ApplyAutoHeight()
    {
        if (!AutoHeight) return;
        if (!Program.WriteAccessAvailable) return;
        if (Program.CamHeightValue.BaseAddress <= 0 || Program.CamPanValue.BaseAddress <= 0) return;

        double yaw = Program.CamPanValue.Read();
        if (double.IsNaN(yaw)) return;

        double correction = AutoHeightGain * Math.Max(0.0, -Math.Sin(yaw / 2.0));
        LastHeightCorrection = correction;

        float target = (float)(BaseHeight + correction);
        float current = Program.CamHeightValue.Read();
        if (float.IsNaN(current) || Math.Abs(current - target) < 1e-5f) return;

        Program.TryChangeValue(Program.CamHeightValue, target);

        // Log only on a meaningful change; this runs a thousand times a second.
        if (double.IsNaN(_lastHeightLogged) || Math.Abs(correction - _lastHeightLogged) > 0.01)
        {
            _lastHeightLogged = correction;
            Program.LogLine($"Auto height: yaw {yaw:F4} ({yaw * 180 / Math.PI:F1} deg) -> "
                            + $"height {BaseHeight + correction:F6} "
                            + $"(base {BaseHeight:F6} + {correction:F6})");
        }
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
    /// Permanent monitor for the value we manage: samples it and reports any change this
    /// control did not make, together with the context at the time.
    ///
    /// This replaced a watch that only ran for 25 s after the last key release. That window
    /// missed the actual trigger: comparing two sessions showed the value surviving a full
    /// 25 s untouched after release, and reverting about a second after the game LOST FOCUS.
    /// A bounded post-release window cannot see an event that happens later, so the monitor
    /// now runs continuously and reports whatever it finds, whenever it finds it.
    /// </summary>
    private void MonitorCamera()
    {
        if (Program.CamPanValue.BaseAddress <= 0) return;

        double nowMs = _clock.Elapsed.TotalMilliseconds;
        if (nowMs < _nextMonitorMs) return;
        _nextMonitorMs = nowMs + MonitorEveryMs;

        float cur = Program.CamPanValue.Read();
        if (float.IsNaN(_lastSeenYaw)) { _lastSeenYaw = cur; return; }
        if (Math.Abs(cur - _lastSeenYaw) <= 1e-6f) return;

        _externalChanges++;
        Program.LogLine(
            $"WASD external yaw change #{_externalChanges}: {_lastSeenYaw:F6} -> {cur:F6} "
            + $"(focused={_gameFocused} holding={AnyKeyHeld} "
            + $"msSinceOurLastWrite={nowMs - _lastWriteMs:F0} mirrorYaw={ReadMirrorYaw():F6})");

        _lastSeenYaw = cur;   // do not report the same change repeatedly
    }

    private static float ReadMirrorYaw()
    {
        foreach (long m in Program.CameraMirrors)
        {
            var v = Program.MemLib.ReadFloat((ulong)(m + 0x8));
            if (v.HasValue) return v.Value;
        }
        return float.NaN;
    }
}
