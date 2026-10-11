using System.Runtime.InteropServices;

namespace D2RCameraTool;

/// <summary>
/// Raises the system timer resolution while the WASD control is actually in use.
///
/// Measured on the target machine: asking System.Threading.Timer for a 1 ms period produced
/// only 62-65 Hz, i.e. the request was floored to the default ~15.6 ms system tick. The
/// interval control was therefore largely illusory at the low end - 16, 8 and 1 all landed
/// on roughly the same real rate.
///
/// timeBeginPeriod is the documented way to lift that floor. It is process-wide in effect on
/// modern Windows but historically system-wide, so it is held only while the game is focused
/// and the feature is on, and released otherwise, rather than for the whole session.
/// </summary>
internal static class TimerResolution
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint uMilliseconds);

    private const uint TargetMs = 1;

    private static readonly object Gate = new();
    private static bool _raised;

    public static bool IsRaised { get { lock (Gate) return _raised; } }

    public static void RequestFineResolution()
    {
        lock (Gate)
        {
            if (_raised) return;
            try
            {
                TimeBeginPeriod(TargetMs);
                _raised = true;
            }
            catch { /* if winmm is unavailable we simply keep the coarse tick */ }
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (!_raised) return;
            try { TimeEndPeriod(TargetMs); } catch { }
            _raised = false;
        }
    }
}
