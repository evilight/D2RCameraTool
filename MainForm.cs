using System.Diagnostics;
using System.Globalization;

namespace D2RCameraTool
{

    public partial class MainForm : Form
    {
        public static readonly double DEFAULT_THROTTLE_SECONDS = 0.01;
        public static readonly float DEFAULT_SENSITIVITY = 0.5f;

        public static bool DisableHotKeys;
        public static bool IsMousePanPressed;
        public static bool IsMouseZoomPressed;

        
        // Next to the executable, not the current working directory: a shortcut whose
        // "Start in" differs, or launching via a shell from another folder, would otherwise
        // silently use (or fail to find) a different settings.ini.
        private static readonly string INI_FILE_NAME =
            Path.Combine(AppContext.BaseDirectory, "settings.ini");

        private static AppConfig IniFile;
        private static CameraPreset[] PresetsArray;
        private static Keys MousePanHotKey = Keys.Space;
        private static Keys MouseZoomHotKey = Keys.Z;
        private static double throttleDelay;
        private static float screenWidth;
        private static float mouseSensitivity;
        private long lastTicks;
        private Point lastMouse;
        private bool isDragging;
        private bool isZoomDragging;
        private float startPitch;
        private float startCamHeight;
        private float startCamPan;
        private float startRoll;
        private float startZoom;

        // ---- WASD camera control ------------------------------------------------
        internal static readonly WasdCameraControl Wasd = new();
        private GroupBox WasdGroup = null!;
        private CheckBox Wasd_CheckBox = null!;
        private NumericUpDown WasdSpeed_UpDown = null!;
        private CheckBox WasdInvertYaw_CheckBox = null!;
        private CheckBox WasdInvertPitch_CheckBox = null!;
        private CheckBox WasdBlock_CheckBox = null!;
        private CheckBox WasdFocus_CheckBox = null!;
        private NumericUpDown WasdRate_UpDown = null!;
        private Label Wasd_Status_Label = null!;
        private GroupBox SessionGroup = null!;
        private Button ResetAll_Button = null!;
        private Button OverShoulder_Button = null!;
        private Button SweepPitch_Button = null!;
        private Button SweepYaw_Button = null!;
        private Button SweepRoll_Button = null!;
        private System.Windows.Forms.Timer? wasdUiTimer;
        private System.Threading.Timer? wasdWriteTimer;
        private bool lastFocusState;
        private string lastWasdSignature = string.Empty;

        public MainForm() {
            InitializeComponent();
            PresetsArray = new CameraPreset[5];
            for (int i = 0; i < PresetsArray.Length; i++) {
                PresetsArray[i] = new CameraPreset();
            }

            if (Presets_Combo.Items.Count > 0) {
                Presets_Combo.SelectedIndex = 0;
            }
        }

        private void InitHotkeys() {
            GlobalHotkeys.HookKeyboard();
            // A freshly installed hook has seen no key events, so any key tracked as held from
            // a previous hook can never receive its key-up. Drop them or the direction sticks.
            Wasd.ClearHeldKeys();
            GlobalHotkeys.KeyPressed += GlobalHotKey_Pressed!;
            GlobalHotkeys.KeyReleased += GlobalHotKey_Released!;
            // Lets the hook swallow W/A/S/D before the game sees them.
            GlobalHotkeys.SuppressKey = Wasd.ShouldSuppress;
        }

        /// <summary>
        /// Builds the WASD panel in code. The designer has no spare space, so the form is
        /// grown and the status strip pushed down rather than moving existing controls.
        /// </summary>
        private void BuildWasdPanel() {
            // Yaw is clamped at +/-pi by every input path, so the spin box must be able to
            // represent that limit. Without widening it the box would clamp the displayed value
            // and its ValueChanged handler would then write that clamped value over the real one.
            CamPan_UpDown.Minimum = (decimal)WasdCameraControl.YawMin;
            CamPan_UpDown.Maximum = (decimal)WasdCameraControl.YawMax;

            const int groupH = 98;
            const int extra = groupH + 8;

            WasdGroup = new GroupBox {
                Text = "WASD camera control",
                Location = new Point(Main_Panel.Left, Main_Panel.Bottom + 6),
                Size = new Size(Main_Panel.Width, groupH),
            };

            Wasd_CheckBox = new CheckBox {
                Text = "Enable WASD", AutoSize = true,
                Location = new Point(10, 22),
            };
            var speedLabel = new Label {
                Text = "Speed (rad/s)", AutoSize = true,
                Location = new Point(150, 24),
            };
            WasdSpeed_UpDown = new NumericUpDown {
                Location = new Point(248, 21), Width = 70,
                DecimalPlaces = 2, Increment = 0.1m,
                Minimum = 0.05m, Maximum = 10m,
                Value = (decimal)WasdCameraControl.DefaultSpeed,
            };
            WasdBlock_CheckBox = new CheckBox {
                Text = "Block W/A/S/D from the game", AutoSize = true,
                Location = new Point(340, 22),
            };

            WasdInvertYaw_CheckBox = new CheckBox {
                Text = "Invert yaw (A/D)", AutoSize = true,
                Location = new Point(10, 48),
            };
            WasdInvertPitch_CheckBox = new CheckBox {
                Text = "Invert pitch (W/S)", AutoSize = true,
                Location = new Point(150, 48),
            };
            WasdFocus_CheckBox = new CheckBox {
                Text = "Only while the game has focus", AutoSize = true,
                Location = new Point(300, 48),
            };
            var rateLabel = new Label {
                Text = "Update (ms)", AutoSize = true,
                Location = new Point(478, 50),
            };
            WasdRate_UpDown = new NumericUpDown {
                Location = new Point(566, 47), Width = 56,
                Minimum = WasdCameraControl.MinUpdateIntervalMs,
                Maximum = WasdCameraControl.MaxUpdateIntervalMs,
                Increment = 1,
                Value = WasdCameraControl.DefaultUpdateIntervalMs,
            };

            Wasd_Status_Label = new Label {
                AutoSize = false,
                Location = new Point(10, 72),
                Size = new Size(Main_Panel.Width - 30, 18),
            };

            WasdGroup.Controls.AddRange(new Control[] {
                Wasd_CheckBox, speedLabel, WasdSpeed_UpDown, WasdBlock_CheckBox,
                WasdInvertYaw_CheckBox, WasdInvertPitch_CheckBox, WasdFocus_CheckBox,
                rateLabel, WasdRate_UpDown, Wasd_Status_Label,
            });

            ClientSize = new Size(ClientSize.Width, ClientSize.Height + extra);
            statusStrip1.Top += extra;
            Controls.Add(WasdGroup);

            Wasd_CheckBox.CheckedChanged += (_, _) => { Wasd.Enabled = Wasd_CheckBox.Checked; UpdateWasdStatus(); };
            WasdSpeed_UpDown.ValueChanged += (_, _) => Wasd.SpeedRadPerSec = (double)WasdSpeed_UpDown.Value;
            WasdInvertYaw_CheckBox.CheckedChanged += (_, _) => Wasd.InvertYaw = WasdInvertYaw_CheckBox.Checked;
            WasdInvertPitch_CheckBox.CheckedChanged += (_, _) => Wasd.InvertPitch = WasdInvertPitch_CheckBox.Checked;
            WasdBlock_CheckBox.CheckedChanged += (_, _) => Wasd.BlockKeys = WasdBlock_CheckBox.Checked;
            WasdFocus_CheckBox.CheckedChanged += (_, _) => { Wasd.RequireGameFocus = WasdFocus_CheckBox.Checked; UpdateWasdStatus(); };
            WasdRate_UpDown.ValueChanged += (_, _) => {
                Wasd.UpdateIntervalMs = (int)WasdRate_UpDown.Value;
                wasdWriteTimer?.Change(0, Wasd.UpdateIntervalMs);
            };

            ApplyWasdToUi();
            UpdateWasdStatus();
        }

        /// <summary>
        /// One-click "put every camera value back": restores pitch, yaw, roll and height, plus
        /// zoom unless it is unticked.
        ///
        /// This exists because the values are what the locator keys on. Leaving pitch or yaw
        /// rotated means the legacy signature no longer matches on the next launch and the tool
        /// has to fall back to the slower structural scan - or fails on a hostile layout. It
        /// writes the pristine 1.6-era byte patterns, which is exactly what restores the
        /// signature, rather than whatever values happened to be present when we attached.
        ///
        /// It deliberately does NOT exit: closing stays a separate, deliberate action.
        /// </summary>
        private void ResetAllValues_Click(object sender, EventArgs e) {
            if (!Program.ValuesFound) {
                Program.LogLine("Reset all requested before the camera was located - nothing to restore");
                return;
            }

            var restored = new List<string>();

            // Detach the change handlers first, exactly as the individual Reset buttons do:
            // otherwise writing the value back through the spin box just re-issues the write.
            PitchSpinner.ValueChanged -= PitchSpinner_ValueChanged!;
            CamPan_UpDown.ValueChanged -= CamPan_UpDown_ValueChanged!;
            Roll_UpDown.ValueChanged -= Roll_UpDown_ValueChanged!;
            CamHeight_UpDown.ValueChanged -= CamHeight_UpDown_ValueChanged!;
            Zoom_UpDown.ValueChanged -= Zoom_UpDown_ValueChanged!;

            try {
                // TryResetValue writes the value AND updates the matching spin box. Using the
                // same helper as the individual Reset buttons is what stops the panel drifting
                // out of step with the game - which is exactly what this button did wrong
                // before: it reset memory only and left the readouts showing the old numbers.
                Program.TryResetValue(Program.PitchValue, PitchSpinner);
                Program.TryResetValue(Program.CamPanValue, CamPan_UpDown);
                Program.TryResetValue(Program.RollValue, Roll_UpDown);
                Program.TryResetValue(Program.CamHeightValue, CamHeight_UpDown);
                restored.AddRange(new[] { "pitch", "yaw", "roll", "height" });

                // Zoom keeps its own guard and its own reset value (0) rather than going through
                // TryResetValue, matching ZoomReset_Button_Click. It is always reset: there is no
                // reason to leave one of the five values changed while putting the other four
                // back, and the option only added a way to get that wrong.
                if (Program.ZoomFound) {
                    Program.ZoomValue.Write(0);
                    Zoom_UpDown.Value = 0;
                    restored.Add("zoom");
                }
            } finally {
                PitchSpinner.ValueChanged += PitchSpinner_ValueChanged!;
                CamPan_UpDown.ValueChanged += CamPan_UpDown_ValueChanged!;
                Roll_UpDown.ValueChanged += Roll_UpDown_ValueChanged!;
                CamHeight_UpDown.ValueChanged += CamHeight_UpDown_ValueChanged!;
                Zoom_UpDown.ValueChanged += Zoom_UpDown_ValueChanged!;
            }

            // Log both sides so a mismatch between panel and game is visible in the file.
            Program.LogLine(
                $"Reset all ({string.Join(", ", restored)}). "
                + $"Panel: pitch={PitchSpinner.Value} yaw={CamPan_UpDown.Value} "
                + $"roll={Roll_UpDown.Value} height={CamHeight_UpDown.Value} zoom={Zoom_UpDown.Value}. "
                + $"Game: pitch={Program.PitchValue.Read():F6} yaw={Program.CamPanValue.Read():F6} "
                + $"roll={Program.RollValue.Read():F6} height={Program.CamHeightValue.Read():F6} "
                + $"zoom={Program.ZoomValue.Read():F6}");
        }

        /// <summary>
        /// Applies the over-the-shoulder pose. Goes through the spin boxes rather than writing
        /// memory directly: their ValueChanged handlers are the existing, tested write path, and
        /// the displayed values stay in step with the game.
        /// </summary>
        private void OverShoulder_Click(object sender, EventArgs e) {
            if (!Program.ValuesFound) {
                Program.LogLine("Over-shoulder pose requested before the camera was located");
                return;
            }

            try {
                PitchSpinner.Value = ClampToRange(PitchSpinner, (decimal)CameraPresets.OverShoulderPitch);
                Roll_UpDown.Value = ClampToRange(Roll_UpDown, (decimal)CameraPresets.OverShoulderRoll);
                Zoom_UpDown.Value = ClampToRange(Zoom_UpDown, (decimal)CameraPresets.OverShoulderZoom);

                Program.LogLine(
                    $"Over-shoulder pose applied: pitch={PitchSpinner.Value} "
                    + $"roll={Roll_UpDown.Value} zoom={Zoom_UpDown.Value} "
                    + $"(yaw={CamPan_UpDown.Value} and height={CamHeight_UpDown.Value} left alone). "
                    + "Read back from game: "
                    + $"pitch={Program.PitchValue.Read():F6} roll={Program.RollValue.Read():F6} "
                    + $"zoom={Program.ZoomValue.Read():F6}");
            } catch (Exception ex) {
                Program.LogLine("Over-shoulder pose failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Compact strip under the WASD panel holding the session buttons. The designer panel
        /// has no spare room (its lowest control ends 11 px above the bottom), so the form is
        /// grown rather than rearranging existing controls.
        /// </summary>
        private void BuildSessionPanel() {
            const int groupH = 46;
            const int extra = groupH + 6;

            SessionGroup = new GroupBox {
                Text = "Session",
                Location = new Point(Main_Panel.Left, WasdGroup.Bottom + 6),
                Size = new Size(Main_Panel.Width, groupH),
            };

            ResetAll_Button = new Button {
                Text = "Reset all values",
                Location = new Point(10, 14),
                Size = new Size(120, 26),
            };
            OverShoulder_Button = new Button {
                Text = "Over-shoulder pose",
                Location = new Point(142, 14),
                Size = new Size(150, 26),
            };
            SweepPitch_Button = new Button {
                Text = "Sweep pitch",
                Location = new Point(304, 14),
                Size = new Size(110, 26),
            };
            SweepYaw_Button = new Button {
                Text = "Sweep yaw",
                Location = new Point(422, 14),
                Size = new Size(110, 26),
            };
            SweepRoll_Button = new Button {
                Text = "Sweep roll",
                Location = new Point(540, 14),
                Size = new Size(110, 26),
            };

            SessionGroup.Controls.AddRange(new Control[] {
                ResetAll_Button, OverShoulder_Button,
                SweepPitch_Button, SweepYaw_Button, SweepRoll_Button,
            });

            ClientSize = new Size(ClientSize.Width, ClientSize.Height + extra);
            statusStrip1.Top += extra;
            Controls.Add(SessionGroup);

            ResetAll_Button.Click += ResetAllValues_Click;
            OverShoulder_Button.Click += OverShoulder_Click;
            SweepPitch_Button.Click += (_, _) => StartSweep("pitch");
            SweepYaw_Button.Click += (_, _) => StartSweep("yaw");
            SweepRoll_Button.Click += (_, _) => StartSweep("roll");
        }

        /// <summary>
        /// Steps one camera angle across its whole range and captures the game window at each
        /// step. It has to run inside this process: the tool holds an elevated handle to the game
        /// and nothing else can open it for writing.
        /// </summary>
        private void StartSweep(string which) {
            if (!Program.ValuesFound || Program.PitchValue.BaseAddress <= 0) {
                Program.LogLine($"Sweep {which} requested before the camera was located");
                return;
            }
            if (!Program.WriteAccessAvailable) {
                Program.LogLine($"Sweep {which} needs write access - run the tool as administrator");
                return;
            }

            SweepPitch_Button.Enabled = SweepYaw_Button.Enabled = SweepRoll_Button.Enabled = false;
            SweepPitch_Button.Text = SweepYaw_Button.Text = SweepRoll_Button.Text = "sweeping...";
            string dir = Path.Combine(AppContext.BaseDirectory, "sweep_" + which);
            Program.LogLine($"Sweep {which}: writing to {dir}");

            System.Threading.Tasks.Task.Run(() => {
                try {
                    Program.RunValueSweep(which, dir, 16, Program.LogLine);
                } catch (Exception ex) {
                    Program.LogLine("Sweep failed: " + ex.Message);
                } finally {
                    try {
                        BeginInvoke(() => {
                            SweepPitch_Button.Enabled = SweepYaw_Button.Enabled = SweepRoll_Button.Enabled = true;
                            SweepPitch_Button.Text = "Sweep pitch";
                            SweepYaw_Button.Text = "Sweep yaw";
                            SweepRoll_Button.Text = "Sweep roll";
                        });
                    } catch { }
                }
            });
        }

        /// <summary>Pushes the control's settings into the widgets (used after loading the ini).</summary>
        private void ApplyWasdToUi() {
            Wasd_CheckBox.Checked = Wasd.Enabled;
            WasdSpeed_UpDown.Value = (decimal)Math.Clamp(Wasd.SpeedRadPerSec,
                (double)WasdSpeed_UpDown.Minimum, (double)WasdSpeed_UpDown.Maximum);
            WasdInvertYaw_CheckBox.Checked = Wasd.InvertYaw;
            WasdInvertPitch_CheckBox.Checked = Wasd.InvertPitch;
            WasdBlock_CheckBox.Checked = Wasd.BlockKeys;
            WasdFocus_CheckBox.Checked = Wasd.RequireGameFocus;
            WasdRate_UpDown.Value = (decimal)Math.Clamp(Wasd.UpdateIntervalMs,
                (int)WasdRate_UpDown.Minimum, (int)WasdRate_UpDown.Maximum);
        }

        private void UpdateWasdStatus() {
            if (Wasd_Status_Label is null) return;
            string focus = Program.ProcessFound
                ? (Wasd.GameFocused ? "game focused - active" : "game not focused - idle")
                : "no process attached";
            Wasd_Status_Label.Text =
                $"A/D = view direction (yaw)    W = look down    S = look up    [{focus}]";
        }

        /// <summary>
        /// Called from the UI timer, never from the write timer. The spinner's change handler
        /// is detached around the assignment because it writes straight back to game memory.
        /// </summary>
        private void ApplyDisplays() {
            if (Wasd.DisplayYawDirty) {
                CamPan_UpDown.ValueChanged -= CamPan_UpDown_ValueChanged!;
                try { CamPan_UpDown.Value = ClampToRange(CamPan_UpDown, (decimal)Wasd.DisplayYaw); }
                finally { CamPan_UpDown.ValueChanged += CamPan_UpDown_ValueChanged!; }
            }

            if (Wasd.DisplayPitchDirty) {
                PitchSpinner.ValueChanged -= PitchSpinner_ValueChanged!;
                try { PitchSpinner.Value = ClampToRange(PitchSpinner, (decimal)Wasd.DisplayPitch); }
                finally { PitchSpinner.ValueChanged += PitchSpinner_ValueChanged!; }
            }

            Wasd.ClearDisplayDirty();
        }

        private static decimal ClampToRange(NumericUpDown box, decimal value) =>
            value < box.Minimum ? box.Minimum : value > box.Maximum ? box.Maximum : value;

        private void DestroyHotkeys() {
            GlobalHotkeys.UnhookKeyboard();
        }

        private void Form1_Load(object sender, EventArgs e) {
            throttleDelay = DEFAULT_THROTTLE_SECONDS;
            UpdateDelay_TextBox.Text = DEFAULT_THROTTLE_SECONDS.ToString();

            mouseSensitivity = DEFAULT_SENSITIVITY;
            Sensitivity_UpDown.Value = (decimal)DEFAULT_SENSITIVITY;

            IniFile = new AppConfig(INI_FILE_NAME);
            if (IniFile != null) {
                // A malformed value (e.g. a bad key name in pan_hotkey) made Enum.Parse throw
                // out of Form1_Load and left the tool half-initialised. Config must never be
                // able to stop the app from starting.
                try {
                    LoadSettingsIni();
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine("settings.ini ignored: " + ex.Message);
                }
            }

            screenWidth = (float)(Screen.PrimaryScreen?.Bounds.Width ?? 1920);

            // After LoadSettingsIni so the loaded WASD settings reach the widgets.
            BuildWasdPanel();
            BuildSessionPanel();
            InitHotkeys();

            TopMost = TopMost_CheckBox.Checked;
        }

        private void Form1_Shown(object sender, EventArgs e) {
            Application.Idle += FormIdle_Tick!;
            Application.Idle += Program.Idle_Tick!;
            Program.FormShown_Callback();

            // A dedicated ~60 Hz timer rather than Application.Idle: the WASD feel depends on
            // a steady cadence, and Idle only fires when the message queue happens to drain.
            // The camera WRITE runs on a threadpool timer, not a WinForms one: WM_TIMER is
            // coalesced to the system tick, so intervals below roughly 10 ms are not honoured.
            // The write rate is exactly the lever that matters here, because the engine
            // re-asserts its own camera value every frame and we are racing it.
            wasdWriteTimer = new System.Threading.Timer(_ => WasdTick(), null, 0, Wasd.UpdateIntervalMs);

            // UI refresh only. Repainting the spin boxes at the write rate is needless churn
            // while the window floats over the game, so the readout lags deliberately.
            wasdUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
            wasdUiTimer.Tick += WasdUi_Tick!;
            wasdUiTimer.Start();

            TopMost = TopMost_CheckBox.Checked;
            UpdateWasdStatus();

            // Record the real geometry once. Both extra panels are built in code and grow the
            // form, so if a control ever ends up clipped or overlapping this says exactly where
            // everything landed instead of leaving it to guesswork.
            Program.LogLine(
                $"Layout: client={ClientSize.Width}x{ClientSize.Height} "
                + $"mainPanel={Main_Panel.Bounds} wasd={WasdGroup.Bounds} "
                + $"session={SessionGroup.Bounds} "
                + $"statusTop={statusStrip1.Top} statusH={statusStrip1.Height}");
        }

        /// <summary>Camera write step. Runs on a threadpool timer; must never touch a control.</summary>
        private void WasdTick() {
            try {
                // Hold a 1 ms system tick only while the feature is actually being used.
                if (Wasd.Enabled && Wasd.GameFocused) TimerResolution.RequestFineResolution();
                else TimerResolution.Release();

                int pid = Program.ProcessFound && Program.MemLib.IsOpen ? Program.MemLib.ProcessId : 0;
                Wasd.Tick(pid);
            } catch {
                // An unhandled exception on a timer thread would take the whole app down.
            }
        }

        /// <summary>UI refresh, on the UI thread and deliberately slower than the write rate.</summary>
        private void WasdUi_Tick(object sender, EventArgs e) {
            if (Wasd.DisplayYawDirty || Wasd.DisplayPitchDirty) ApplyDisplays();

            int pid = Program.ProcessFound && Program.MemLib.IsOpen ? Program.MemLib.ProcessId : 0;

            // Log the gates only on a transition. Logging on a timer produced a line every
            // 700 ms for as long as a key was held, which turns an hour of play into thousands
            // of lines and buries the events that actually matter.
            string signature = Wasd.Signature();
            if (signature != lastWasdSignature) {
                lastWasdSignature = signature;
                Program.LogLine("WASD " + Wasd.DescribeGates());
            }

            if (Wasd.GameFocused != lastFocusState) {
                lastFocusState = Wasd.GameFocused;
                UpdateWasdStatus();
            }
        }

        private void TopMost_CheckedChanged(object sender, EventArgs e) {
            TopMost = TopMost_CheckBox.Checked;
        }

        private void ProgramEnabled_CheckBox_CheckedChanged(object sender, EventArgs e) {
            if (ProgramEnabled_CheckBox.Checked) {
                Program.EnableChecked();
            } else {
                Program.EnableUnchecked();
            }
        }

        private void PitchSpinner_ValueChanged(object sender, EventArgs e) {
            Program.TryChangeValue(Program.PitchValue, (float)PitchSpinner.Value, PitchSpinner);
        }

        private void CamHeight_UpDown_ValueChanged(object sender, EventArgs e) {
            Program.TryChangeValue(Program.CamHeightValue, (float)CamHeight_UpDown.Value, CamHeight_UpDown);
        }

        private void PitchReset_Click(object sender, EventArgs e) {
            PitchSpinner.ValueChanged -= PitchSpinner_ValueChanged!;
            Program.TryResetValue(Program.PitchValue, PitchSpinner);
            PitchSpinner.ValueChanged += PitchSpinner_ValueChanged!;
        }

        private void CamHeight_Reset_Click(object sender, EventArgs e) {
            CamHeight_UpDown.ValueChanged -= CamHeight_UpDown_ValueChanged!;
            Program.TryResetValue(Program.CamHeightValue, CamHeight_UpDown);
            CamHeight_UpDown.ValueChanged += CamHeight_UpDown_ValueChanged!;
        }

        private void CamPan_Reset_Click(object sender, EventArgs e) {
            CamPan_UpDown.ValueChanged -= CamPan_UpDown_ValueChanged!;
            Program.TryResetValue(Program.CamPanValue, CamPan_UpDown);
            CamPan_UpDown.ValueChanged += CamPan_UpDown_ValueChanged!;
        }

        private void RollReset_Button_Click(object sender, EventArgs e) {
            Roll_UpDown.ValueChanged -= Roll_UpDown_ValueChanged!;
            Program.TryResetValue(Program.RollValue, Roll_UpDown);
            Roll_UpDown.ValueChanged += Roll_UpDown_ValueChanged!;
        }

        private void ZoomReset_Button_Click(object sender, EventArgs e) {
            if (!Program.ZoomFound)
                return;

            Zoom_UpDown.ValueChanged -= Zoom_UpDown_ValueChanged!;
            Program.ZoomValue.Write(0);
            Zoom_UpDown.Value = 0;
            Zoom_UpDown.ValueChanged += Zoom_UpDown_ValueChanged!;
        }

        private void PitchDragButton_MouseDown(object sender, MouseEventArgs e) {
            StartPitchDragging();
            isDragging = true;
        }

        private void StartPitchDragging() {
            lastMouse = Control.MousePosition;
            //startMouse = PitchDragButton.PointToClient(Control.MousePosition);
            startPitch = Program.PitchValue.Read();
            startCamHeight = Program.CamHeightValue.Read();
            startCamPan = Program.CamPanValue.Read();
            startRoll = Program.RollValue.Read();
            startZoom = Program.ZoomValue.Read();
            lastTicks = 0;
        }

        private void StartZoomDragging() {
            lastMouse = Control.MousePosition;
            //startMouse = PitchDragButton.PointToClient(Control.MousePosition);
            startZoom = Program.ZoomValue.Read();
            lastTicks = 0;
        }

        private void PitchDragButton_MouseUp(object sender, MouseEventArgs e) {
            isDragging = false;
            lastTicks = 0;
        }

        private void GlobalHotKey_Pressed(object sender, KeyEventArgs e) {
            // Feed the WASD control from the hook as well as from GetAsyncKeyState: a key this
            // hook swallows may never reach the input queue the polling API reads.
            Wasd.OnKeyPressed(e.KeyCode);

            if (MousePanHotKey != Keys.None && e.KeyCode == MousePanHotKey) {
                if (!IsMousePanPressed) {
                    StartPitchDragging();
                }
                IsMousePanPressed = true;
            } else if (MouseZoomHotKey != Keys.None && e.KeyCode == MouseZoomHotKey) {
                if (!IsMouseZoomPressed) {
                    StartZoomDragging();
                }
                IsMouseZoomPressed = true;
            }
        }

        private void GlobalHotKey_Released(object sender, KeyEventArgs e) {
            Wasd.OnKeyReleased(e.KeyCode);

            if (MousePanHotKey != Keys.None && e.KeyCode == MousePanHotKey) {
                IsMousePanPressed = false;
            } else if (MouseZoomHotKey != Keys.None && e.KeyCode == MouseZoomHotKey) {
                IsMouseZoomPressed = false;
            }
        }

        private void ZoomDrag_Tick() {
            if (!Program.ProgramEnabled || !Program.ZoomFound) {
                isZoomDragging = false;
                return;
            }

            long updateDelayInTicks = (long)(throttleDelay * TimeSpan.TicksPerSecond);
            long currentTicks = DateTime.Now.Ticks;
            long elapsedTicks = currentTicks - lastTicks;
            if (elapsedTicks < updateDelayInTicks)
                return;
            lastTicks = currentTicks;

            Point currentMousePosition = Control.MousePosition;
            int delta = currentMousePosition.X - lastMouse.X;
            lastMouse = currentMousePosition;
            float deltaValue = delta / screenWidth;

            float zoom = SmoothTransform(delta, Program.ZoomValue.Read(), -0.249f, 2.0f, mouseSensitivity);
            if (Program.TryChangeZoom(zoom, Zoom_UpDown)) {
                Zoom_UpDown.ValueChanged -= Zoom_UpDown_ValueChanged!;
                Zoom_UpDown.Value = (decimal)zoom;
                Zoom_UpDown.ValueChanged += Zoom_UpDown_ValueChanged!;
            }
        }

        private void PitchDrag_Tick() {
            if (!Program.ProgramEnabled || !Program.ValuesFound) {
                isDragging = false;
                return;
            }

            long updateDelayInTicks = (long)(throttleDelay * TimeSpan.TicksPerSecond);
            long currentTicks = DateTime.Now.Ticks;
            long elapsedTicks = currentTicks - lastTicks;
            if (elapsedTicks < updateDelayInTicks)
                return;
            lastTicks = currentTicks;

            Point currentMousePosition = Control.MousePosition;
            int delta = currentMousePosition.X - lastMouse.X;
            lastMouse = currentMousePosition;
            float deltaValue = delta / screenWidth;

            if (PitchAngle_CheckBox.Checked) {
                float pitchResult = SmoothTransform(delta, Program.PitchValue.Read(), -2.0f, 2.0f, mouseSensitivity);
                if (Program.TryChangeValue(Program.PitchValue, pitchResult, PitchSpinner)) {
                    PitchSpinner.ValueChanged -= PitchSpinner_ValueChanged!;
                    PitchSpinner.Value = (decimal)pitchResult;
                    PitchSpinner.ValueChanged += PitchSpinner_ValueChanged!;
                }
            }
            if (CamHeight_CheckBox.Checked) {
                float camHeightResult = SmoothTransform(delta, Program.CamHeightValue.Read(), -2.0f, 2.0f, mouseSensitivity);
                if (Program.TryChangeValue(Program.CamHeightValue, camHeightResult, CamHeight_UpDown)) {
                    CamHeight_UpDown.ValueChanged -= CamHeight_UpDown_ValueChanged!;
                    CamHeight_UpDown.Value = (decimal)camHeightResult;
                    CamHeight_UpDown.ValueChanged += CamHeight_UpDown_ValueChanged!;
                }
            }
            if (CamPan_CheckBox.Checked) {
                float camPanResult = SmoothTransform(delta, Program.CamPanValue.Read(),
                    (float)WasdCameraControl.YawMin, (float)WasdCameraControl.YawMax, mouseSensitivity);
                if (Program.TryChangeValue(Program.CamPanValue, camPanResult, CamPan_UpDown)) {
                    CamPan_UpDown.ValueChanged -= CamPan_UpDown_ValueChanged!;
                    CamPan_UpDown.Value = (decimal)camPanResult;
                    CamPan_UpDown.ValueChanged += CamPan_UpDown_ValueChanged!;
                }
            }
            if (Roll_CheckBox.Checked) {
                float rollResult = SmoothTransform(delta, Program.RollValue.Read(), -2.0f, 2.0f, mouseSensitivity);
                if (Program.TryChangeValue(Program.RollValue, rollResult, Roll_UpDown)) {
                    Roll_UpDown.ValueChanged -= Roll_UpDown_ValueChanged!;
                    Roll_UpDown.Value = (decimal)rollResult;
                    Roll_UpDown.ValueChanged += Roll_UpDown_ValueChanged!;
                }
            }
            if (Zoom_CheckBox.Checked && Program.ZoomFound) {
                float zoom = SmoothTransform(delta, Program.ZoomValue.Read(), (float)Zoom_UpDown.Minimum, (float)Zoom_UpDown.Maximum, mouseSensitivity);
                if (Program.TryChangeZoom(zoom, Zoom_UpDown)) {
                    Zoom_UpDown.ValueChanged -= Zoom_UpDown_ValueChanged!;
                    Zoom_UpDown.Value = (decimal)zoom;
                    Zoom_UpDown.ValueChanged += Zoom_UpDown_ValueChanged!;
                }
            }
        }

        private void FormIdle_Tick(object sender, EventArgs e) {
            if (isDragging || IsMousePanPressed) {
                PitchDrag_Tick();
            } else if (isZoomDragging || IsMouseZoomPressed) {
                ZoomDrag_Tick();
            }

        }

        private static float SmoothTransform(int delta, float current, float minValue, float maxValue, float rate) {
            float scaledDelta = delta / screenWidth;
            float deltaValue = scaledDelta * rate;
            float result = current + deltaValue;
            result = Math.Clamp(result, minValue, maxValue);

            return result;
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e) {
            // Release everything held on the machine's behalf before the process goes away:
            // the write timer, and the raised system timer resolution.
            wasdWriteTimer?.Dispose();
            wasdWriteTimer = null;
            wasdUiTimer?.Stop();
            TimerResolution.Release();
            DestroyHotkeys();
            SaveSettingsIni();
        }

        private void HotKeysEnabled_CheckBox_CheckedChanged(object sender, EventArgs e) {
            DisableHotKeys = !HotKeysEnabled_CheckBox.Checked;
        }

        private void MouseHotKey_TextBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode != Keys.None) {
                MouseHotKey_TextBox.Text = e.KeyCode.ToString();
                System.Threading.Timer? delay = null;
                delay = new System.Threading.Timer((state) => {
                    MousePanHotKey = e.KeyCode;
                    delay?.Dispose();
                }, null, 1000, 0);
            }
            Program.PlayBeepSound();
        }

        private void ZoomHotKey_TextBox_KeyDown(object sender, KeyEventArgs e) {
            if (e.KeyCode != Keys.None) {
                ZoomHotKey_TextBox.Text = e.KeyCode.ToString();
                System.Threading.Timer? delay = null;
                delay = new System.Threading.Timer((state) => {
                    MouseZoomHotKey = e.KeyCode;
                    delay?.Dispose();
                }, null, 1000, 0);
            }
            Program.PlayBeepSound();
        }

        private void CamPan_UpDown_ValueChanged(object sender, EventArgs e) {
            Program.TryChangeValue(Program.CamPanValue, (float)CamPan_UpDown.Value, CamPan_UpDown);
        }

        private void Roll_UpDown_ValueChanged(object sender, EventArgs e) {
            Program.TryChangeValue(Program.RollValue, (float)Roll_UpDown.Value, Roll_UpDown);
        }

        private void Zoom_UpDown_ValueChanged(object sender, EventArgs e) {
            Program.TryChangeZoom((float)Zoom_UpDown.Value, Zoom_UpDown);
        }

        private void SensitivityReset_Button_Click(object sender, EventArgs e) {
            Sensitivity_UpDown.Value = (decimal)DEFAULT_SENSITIVITY;
            mouseSensitivity = DEFAULT_SENSITIVITY;
        }

        private void Sensitivity_UpDown_ValueChanged(object sender, EventArgs e) {
            mouseSensitivity = (float)Sensitivity_UpDown.Value;
        }

        private void UpdateDelay_TextBox_TextChanged(object sender, EventArgs e) {
            if (double.TryParse(UpdateDelay_TextBox.Text, out var t)) {
                throttleDelay = t;
            } else {
                throttleDelay = DEFAULT_THROTTLE_SECONDS;
            }
        }

        private void ZoomDrag_Button_MouseUp(object sender, MouseEventArgs e) {
            isZoomDragging = false;
            lastTicks = 0;
        }

        private void ZoomDrag_Button_MouseDown(object sender, MouseEventArgs e) {
            StartZoomDragging();
            isZoomDragging = true;
        }

        private void Main_Panel_Paint(object sender, PaintEventArgs e) {

        }

        private void Save_Button_Click(object sender, EventArgs e) {
            int i = Presets_Combo.SelectedIndex - 1;
            if (i < 0)
                return;

            SavePresetValues(PresetsArray[i]);

            Status_Label.Text = "Presets saved to slot " + Presets_Combo.SelectedIndex.ToString();
        }

        private void LoadPresetValues(CameraPreset preset) {
            if (preset == null || !Program.ValuesFound)
                return;

            Sensitivity_UpDown.Value = (decimal)preset.Sensitivity;
            UpdateDelay_TextBox.Text = preset.Throttle.ToString();

            PitchSpinner.Value = (decimal)preset.Pitch;
            CamPan_UpDown.Value = (decimal)preset.Yaw;
            Roll_UpDown.Value = (decimal)preset.Roll;
            CamHeight_UpDown.Value = (decimal)preset.Height;

            if (preset.ZoomHotKey != Keys.None) {
                MouseZoomHotKey = preset.ZoomHotKey;
                ZoomHotKey_TextBox.Text = preset.ZoomHotKey.ToString();
            }                
            if (preset.PanHotKey != Keys.None) {
                MousePanHotKey = preset.PanHotKey;
                MouseHotKey_TextBox.Text = preset.PanHotKey.ToString();
            }                

            if (Program.ZoomFound && preset.Zoom != float.MaxValue) {
                Zoom_UpDown.Value = (decimal)preset.Zoom;
            }
        }

        private void ResetDefaultValues() {
            UpdateDelay_TextBox.Text = DEFAULT_THROTTLE_SECONDS.ToString();
            Sensitivity_UpDown.Value = (decimal)DEFAULT_SENSITIVITY;

            if (Program.ValuesFound) {
                Program.PitchValue.Reset();
                PitchSpinner.Value = (decimal)Program.PitchValue.Read();
                Program.PitchValue.Reset();

                Program.CamPanValue.Reset();
                CamPan_UpDown.Value = (decimal)Program.CamPanValue.Read();
                Program.CamPanValue.Reset();

                Program.CamHeightValue.Reset();
                CamHeight_UpDown.Value = (decimal)Program.CamHeightValue.Read();
                Program.CamHeightValue.Reset();

                Program.RollValue.Reset();
                Roll_UpDown.Value = (decimal)Program.RollValue.Read();
                Program.RollValue.Reset();
            }
            if (Program.ZoomFound) {
                Program.ZoomValue.Reset();
                Zoom_UpDown.Value = (decimal)Program.ZoomValue.Read();
                Program.ZoomValue.Reset();
            }
        }

        private void SavePresetValues(CameraPreset preset) {
            if (preset == null || !Program.ValuesFound)
                return;

            Trace.WriteLine("Saving preset values");

            preset.Sensitivity = mouseSensitivity;
            preset.Throttle = throttleDelay;

            preset.Pitch = (float)PitchSpinner.Value;
            preset.Yaw = (float)CamPan_UpDown.Value;
            preset.Roll = (float)Roll_UpDown.Value;
            preset.Height = (float)CamHeight_UpDown.Value;

            preset.ZoomHotKey = MouseZoomHotKey;
            preset.PanHotKey = MousePanHotKey;

            if (Program.ZoomFound) {
                preset.Zoom = (float)Zoom_UpDown.Value;
            } else {
                preset.Zoom = float.MaxValue;
            }
        }

        private void Load_Button_Click(object sender, EventArgs e) {
            int i = Presets_Combo.SelectedIndex;
            if (i == 0) {
                Sensitivity_UpDown.Value = (decimal)DEFAULT_SENSITIVITY;
                UpdateDelay_TextBox.Text = DEFAULT_THROTTLE_SECONDS.ToString();
                ResetDefaultValues();
                Program.PlayBeepSound();
                Status_Label.Text = "Game default values loaded";
            } else {
                LoadPresetValues(PresetsArray[i - 1]);
                Status_Label.Text = "Loaded preset slot " + i.ToString();
            }
        }

        private void DonateLink_Label_Click(object sender, EventArgs e) {
            Program.OpenUrl(DonateLink_Label.Text ?? string.Empty);
        }

        private void SaveSettingsIni() {
            if (IniFile == null)
                return;

            Dictionary<string, string> data = new Dictionary<string, string>();
            // Invariant culture throughout: the default formatting of a float follows the
            // user's locale, so a comma-decimal locale would write values the parser below
            // could not read back.
            data["sensitivity"] = mouseSensitivity.ToString(CultureInfo.InvariantCulture);
            data["zoom_hotkey"] = MouseZoomHotKey.ToString();
            data["pan_hotkey"] = MousePanHotKey.ToString();
            data["throttle"] = throttleDelay.ToString(CultureInfo.InvariantCulture);

            data["wasd_enabled"] = Wasd.Enabled ? "1" : "0";
            data["wasd_speed"] = Wasd.SpeedRadPerSec.ToString(CultureInfo.InvariantCulture);
            data["wasd_invert_yaw"] = Wasd.InvertYaw ? "1" : "0";
            data["wasd_invert_pitch"] = Wasd.InvertPitch ? "1" : "0";
            data["wasd_block_keys"] = Wasd.BlockKeys ? "1" : "0";
            data["wasd_require_focus"] = Wasd.RequireGameFocus ? "1" : "0";
            data["wasd_update_ms"] = Wasd.UpdateIntervalMs.ToString(CultureInfo.InvariantCulture);

            for (int i=0; i<PresetsArray.Length; i++) {
                float[] preset = { PresetsArray[i].Sensitivity, PresetsArray[i].Pitch, PresetsArray[i].Yaw, PresetsArray[i].Roll, PresetsArray[i].Height, PresetsArray[i].Zoom };
                data["preset" + (i+1).ToString()] = JoinFloats(preset);
            }
            

            IniFile.Save(data);
        }

        private void LoadSettingsIni() {
            if (IniFile == null)
                return;

            Dictionary<string, string> data = IniFile.Load();
            float[] preset;

            foreach (var pair in data) {
                switch (pair.Key) {
                    case "sensitivity":
                        if (TryParseInvariant(pair.Value, out float result)) {
                            Sensitivity_UpDown.Value = (decimal)result;
                            mouseSensitivity = result;
                        }
                        break;
                    case "zoom_hotkey":
                        Keys key = (Keys)Enum.Parse(typeof(Keys), pair.Value);
                        if (key != Keys.None) {
                            MouseZoomHotKey = key;
                            ZoomHotKey_TextBox.Text = pair.Value;
                        }
                        break;
                    case "pan_hotkey":
                        Keys panKey = (Keys)Enum.Parse(typeof(Keys), pair.Value);
                        if (panKey != Keys.None) {
                            MousePanHotKey = panKey;
                            ZoomHotKey_TextBox.Text = pair.Value;
                        }
                        break;
                    case "throttle":
                        UpdateDelay_TextBox.Text = pair.Value;
                        break;
                    case "wasd_enabled":
                        Wasd.Enabled = pair.Value == "1";
                        break;
                    case "wasd_speed":
                        if (TryParseInvariant(pair.Value, out double speed) && speed > 0)
                            Wasd.SpeedRadPerSec = speed;
                        break;
                    case "wasd_invert_yaw":
                        Wasd.InvertYaw = pair.Value == "1";
                        break;
                    case "wasd_invert_pitch":
                        Wasd.InvertPitch = pair.Value == "1";
                        break;
                    case "wasd_block_keys":
                        Wasd.BlockKeys = pair.Value == "1";
                        break;
                    case "wasd_require_focus":
                        Wasd.RequireGameFocus = pair.Value == "1";
                        break;
                    case "wasd_update_ms":
                        if (int.TryParse(pair.Value, out int ms) && ms >= 4 && ms <= 100)
                            Wasd.UpdateIntervalMs = ms;
                        break;
                    case "preset1":
                        preset = StringToFloats(pair.Value);
                        FloatsToPreset(preset, 0);
                        break;
                    case "preset2":
                        preset = StringToFloats(pair.Value);
                        FloatsToPreset(preset, 1);
                        break;
                    case "preset3":
                        preset = StringToFloats(pair.Value);
                        FloatsToPreset(preset, 2);
                        break;
                    case "preset4":
                        preset = StringToFloats(pair.Value);
                        FloatsToPreset(preset, 3);
                        break;
                    case "preset5":
                        preset = StringToFloats(pair.Value);
                        FloatsToPreset(preset, 4);
                        break;

                }
                // MessageBox.Show($"Key: {pair.Key}, Value: {pair.Value}");
            }
        }

        private static void FloatsToPreset(float[] preset, int i) {
            if (i >= PresetsArray.Length)
                return;
            PresetsArray[i].Sensitivity = preset[0];
            PresetsArray[i].Pitch = preset[1];
            PresetsArray[i].Yaw = preset[2];
            PresetsArray[i].Roll = preset[3];
            PresetsArray[i].Height = preset[4];
            PresetsArray[i].Zoom = preset[5];

        }

        private static bool TryParseInvariant(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static bool TryParseInvariant(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        public static float[] StringToFloats(string str) {
            string[] parts = str.Split(',');
            float[] values = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++) {
                if (TryParseInvariant(parts[i], out float value)) {
                    values[i] = value;
                } else {
                    throw new ArgumentException("Invalid float value in the input string.");
                }
            }
            return values;
        }
        private static string JoinFloats(float[] values) =>
            string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    }
}
