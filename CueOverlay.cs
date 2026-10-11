using System.Drawing;
using System.Windows.Forms;

namespace D2RCameraTool;

/// <summary>
/// A large instruction banner drawn over everything, used to cue the person during a measurement.
///
/// Sound was tried first and could not be relied on: nothing was audible, most likely because the
/// game holds the audio device and masks anything else. The tool window is already topmost, so a
/// second topmost window works as a cue without any audio at all.
///
/// It must never take focus. The WASD gate refuses to write unless the game is the foreground
/// window, so a cue that activated itself would silently break the very measurement it is
/// announcing. WS_EX_NOACTIVATE plus ShowWithoutActivation keeps it out of the way.
/// </summary>
internal sealed class CueOverlay : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Label _label;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public CueOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(12, 12, 14);
        Size = new Size(720, 132);
        Padding = new Padding(6);

        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 38F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
        };
        Controls.Add(_label);
    }

    /// <summary>Shows the banner with the given text. Safe to call from any thread.</summary>
    public void Cue(string text, Color colour)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(() => Cue(text, colour)); } catch { }
            return;
        }

        _label.Text = text;
        _label.ForeColor = colour;

        Rectangle area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 60);

        if (!Visible) Show();
        // TopMost only holds while the window is not re-created, and the game can push itself
        // forward, so re-assert it on every cue.
        TopMost = false;
        TopMost = true;
        _label.Refresh();
    }

    /// <summary>Hides the banner. Safe to call from any thread.</summary>
    public void Clear()
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(Clear); } catch { }
            return;
        }
        Hide();
    }
}
