using System.Runtime.InteropServices;

namespace D2RCameraTool;

/// <summary>
/// Small native-input helpers for the WASD camera control.
///
/// Key state is read with GetAsyncKeyState rather than tracked from the keyboard hook.
/// The hook only reports transitions, so a missed key-up (Windows silently removes a
/// low-level hook that times out) would leave a direction stuck on forever. Polling the
/// physical state cannot get stuck.
/// </summary>
internal static class NativeInput
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>True while the physical key is held, regardless of which window has focus.</summary>
    public static bool IsDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    /// <summary>Process owning the foreground window, or 0 if it cannot be determined.</summary>
    public static int ForegroundProcessId()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }
}
