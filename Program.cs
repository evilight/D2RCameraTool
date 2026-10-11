using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace D2RCameraTool
{
    internal static class Program
    {
        /// <summary>Win32 memory access, replacing the former Memory.dll dependency.</summary>
        public static Win32Memory MemLib = new();

        public static bool ProgramEnabled = true;
        public static bool ProcessFound;

        /// <summary>
        /// False when the process could only be opened read-only. Detection must not require
        /// write access: an anti-cheat protected game frequently allows PROCESS_VM_READ and
        /// refuses PROCESS_VM_WRITE, and demanding write access up front made such a game
        /// look absent, leaving the status label stuck on its initial text forever.
        /// </summary>
        public static bool WriteAccessAvailable = true;
        public static bool IsAngleScanning;
        public static bool IsZoomScanning;
        public static bool ValuesFound;
        public static bool ZoomFound;

        public static MainForm MainFormInstance = null!;

        public static FloatValue PitchValue = null!;
        public static FloatValue CamHeightValue = null!;
        public static FloatValue CamPanValue = null!;
        public static FloatValue RollValue = null!;
        public static FloatValue ZoomValue = null!;

        public static string LastScanLog = string.Empty;

        // ------------------------------------------------------------ entry point

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Any(a => a.Equals("--sampler-selftest", StringComparison.OrdinalIgnoreCase)))
            {
                SampleDiffSelfTest();
                return 0;
            }

            if (args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
            {
                return RunCli(args);
            }

            ApplicationConfiguration.Initialize();
            MainFormInstance = new MainForm();
            Application.Run(MainFormInstance);
            return 0;
        }

        /// <summary>
        /// Exercises the sample diff against constructed data, so the heading-detection logic can
        /// be checked without a game attached. Writes its result to a file rather than the
        /// console: this is a Windows-subsystem binary, so Console.WriteLine has nowhere to go
        /// unless a parent console is attached first.
        /// </summary>
        private static void SampleDiffSelfTest()
        {
            static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

            var a = new CameraSampler.Sample { Index = 1, Base = 0x1000, Yaw = 0.2f };
            var b = new CameraSampler.Sample { Index = 2, Base = 0x1000, Yaw = 0.2f };

            // The heading signature: an angle that reversed.
            a.Words[0x000] = Bits(0.30f);
            b.Words[0x000] = Bits(0.30f + (float)Math.PI);

            // A reversed angle reached by wrapping past -pi rather than by adding.
            a.Words[0x004] = Bits(-3.00f);
            b.Words[0x004] = Bits(0.20f);

            // Things that must NOT be reported as a heading.
            a.Words[0x008] = Bits(1234.5f);   // a coordinate
            b.Words[0x008] = Bits(1236.0f);
            a.Words[0x00C] = 42u;             // a counter
            b.Words[0x00C] = 43u;
            a.Words[0x010] = Bits(0.5f);      // unchanged
            b.Words[0x010] = Bits(0.5f);

            string report = CameraSampler.BuildDiffReport(a, b);

            // Only the reversed-candidates section matters for the assertion. The coordinate and
            // the counter are EXPECTED to appear further down as context - the point is that they
            // are not claimed as headings.
            int cut = report.IndexOf("largest other changes", StringComparison.Ordinal);
            string reversedSection = cut >= 0 ? report.Substring(0, cut) : report;
            bool ok = reversedSection.Contains("0x000000") && reversedSection.Contains("0x000004")
                      && !reversedSection.Contains("0x000008") && !reversedSection.Contains("0x00000C");

            var text = new System.Text.StringBuilder();
            text.AppendLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
            text.AppendLine(ok
                ? "  both reversed angles flagged; coordinate and counter NOT flagged"
                : "  expectations not met, see the report below");
            text.AppendLine();
            text.AppendLine(report);

            string path = Path.Combine(AppContext.BaseDirectory, "sampler_selftest.txt");
            try { File.WriteAllText(path, text.ToString()); } catch { }
        }

        // ------------------------------------------------------------ CLI mode

        /// <summary>
        /// Headless locate-only mode: attaches read-only, runs the locator and writes a
        /// JSON report. Used to re-derive signatures after a game update.
        /// </summary>
        private static int RunCli(string[] args)
        {
            AttachConsole(-1);

            string process = ArgValue(args, "--process") ?? "D2R";
            string outPath = ArgValue(args, "--out") ?? "d2rcamera_scan.json";
            bool writeAccess = args.Any(a => a.Equals("--allow-write", StringComparison.OrdinalIgnoreCase));
            bool forceStructural = args.Any(a => a.Equals("--force-structural", StringComparison.OrdinalIgnoreCase));
            string? pidArg = ArgValue(args, "--pid");

            var log = new StringBuilder();
            void Log(string s)
            {
                Console.WriteLine(s);
                log.AppendLine(s);
            }

            Log($"D2RCameraTool CLI  process={process}  writeAccess={writeAccess}");

            Win32Memory.Trace = Log;

            bool opened = pidArg is not null && int.TryParse(pidArg, out int pid)
                ? MemLib.Open(pid, writeAccess)
                : MemLib.OpenByName(process, writeAccess);

            if (!opened)
            {
                Log($"could not open '{process}' (win32 err {MemLib.LastOpenError}; "
                    + "5 = access denied, 87 = bad pid, 0 = process not enumerated)");
                return 2;
            }
            Log($"attached pid {MemLib.ProcessId}");

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            LocateResult res;
            try
            {
                res = CameraLocator.Locate(MemLib, Log, cts.Token, forceStructural);
            }
            catch (OperationCanceledException)
            {
                Log("cancelled");
                return 3;
            }

            var report = new
            {
                process,
                pid = MemLib.ProcessId,
                found = res.Found,
                method = res.Method,
                best = res.Best is null ? null : new
                {
                    address = "0x" + res.Best.Base.ToString("X"),
                    res.Best.Roll,
                    res.Best.Pitch,
                    res.Best.Height,
                    res.Best.Yaw,
                    res.Best.LegacyExactFields,
                    res.Best.Churn,
                    res.Best.Score,
                },
                // Byte-exact copies other than the ranked best. These are what the WASD control
                // writes alongside the best address, so it is worth seeing them in the report.
                mirrors = res.Candidates
                    .Where(c => c.LegacyExactFields >= 4 && res.Best is not null
                                && (long)c.Base != (long)res.Best.Base)
                    .Select(c => "0x" + c.Base.ToString("X"))
                    .ToList(),
                candidates = res.Candidates.Take(50).Select(c => new                {
                    address = "0x" + c.Base.ToString("X"),
                    c.Roll,
                    c.Pitch,
                    c.Height,
                    c.Yaw,
                    c.LegacyExactFields,
                    c.Churn,
                    c.ChurnWide,
                    c.Score,
                }),
                log = log.ToString().Split('\n'),
            };

            // Optional write probe: the only fully conclusive test of "is this the camera"
            // is to move it and see the view move. Requires --allow-write.
            string? probeArg = ArgValue(args, "--probe");
            if (probeArg is not null)
            {
                if (!writeAccess)
                {
                    Log("--probe needs --allow-write");
                }
                else if (!int.TryParse(probeArg, out int probeIndex)
                         || probeIndex < 0 || probeIndex >= res.Candidates.Count)
                {
                    Log($"probe index out of range (0..{res.Candidates.Count - 1})");
                }
                else
                {
                    ProbeCandidate(res.Candidates[probeIndex], probeIndex, Log);
                }
            }

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            try
            {
                File.WriteAllText(outPath, json);
                Log($"wrote {outPath}");
            }
            catch (Exception ex)
            {
                Log("could not write report: " + ex.Message);
                Console.WriteLine(json);
            }

            return res.Found ? 0 : 1;
        }

        /// <summary>
        /// Writes a visible pitch offset to a candidate, then restores it. Watching the
        /// in-game view is the only conclusive way to tell a real camera field from a
        /// stale copy of the old constants.
        /// </summary>
        private static void ProbeCandidate(CameraCandidate c, int index, Action<string> log)
        {
            var orig = MemLib.ReadFloat(c.Base);
            if (orig is null) { log($"probe #{index}: could not read 0x{c.Base:X}"); return; }

            const float delta = -0.35f;
            float poked = orig.Value + delta;

            log($"PROBE #{index} 0x{c.Base:X}: pitch {orig.Value:F6} -> {poked:F6}");
            log($"  write accepted: {MemLib.WriteFloat(c.Base, poked)}");
            Thread.Sleep(1500);
            var during = MemLib.ReadFloat(c.Base);
            log($"  read back while poked: {(during?.ToString("F6") ?? "unreadable")}");

            MemLib.WriteFloat(c.Base, orig.Value);
            Thread.Sleep(300);
            var after = MemLib.ReadFloat(c.Base);
            log($"  restored to: {(after?.ToString("F6") ?? "unreadable")}");
            log("  If the view visibly tilted for ~1.5s and then snapped back, this is the camera.");
        }

        private static string? ArgValue(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        // ------------------------------------------------------------ GUI plumbing

        public static void EnableChecked() => ProgramEnabled = true;

        public static void EnableUnchecked()
        {
            ProgramEnabled = false;
            // Say so: with the master switch off the status label never changes, which is
            // indistinguishable from a hung scan.
            Report("Disabled - tick the checkbox to start scanning");
        }

        public static void FormShown_Callback() => Init();

        private static void Init()
        {
            InitValues();
            GlobalTimers.Reset();
        }

        private static void InitValues()
        {
            PitchValue = new FloatValue(CameraLocator.DefaultPitchBytes);
            CamHeightValue = new FloatValue(CameraLocator.DefaultHeightBytes);
            CamPanValue = new FloatValue(CameraLocator.DefaultYawBytes);
            RollValue = new FloatValue(CameraLocator.DefaultRollBytes);
            ZoomValue = new FloatValue(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        }

        public static void Idle_Tick(object sender, EventArgs e) { }

        public static void ProcessScan_Tick()
        {
            if (!ProgramEnabled || ProcessFound) return;

            string process = (MainFormInstance.ProcessName_Text.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(process))
            {
                Report("Type a process name (e.g. D2R.exe) or a PID number");
                return;
            }

            // A numeric entry is treated as a PID. On a locked-down host the process
            // enumeration may only report a couple of dozen processes while OpenProcess by
            // pid still succeeds, so allowing a direct pid bypasses the lookup entirely.
            if (int.TryParse(process, out int directPid))
            {
                if (TryAttachPid(directPid, out string why))
                {
                    ProcessFound = true;
                    MainFormInstance.ProcessName_Text.Enabled = false;
                    Report(why);
                    PlayBeepSound();
                }
                else
                {
                    Report($"Waiting for pid {directPid}: {why}");
                }
                return;
            }

            if (MemLib.OpenByName(process, writeAccess: true))
            {
                ProcessFound = true;
                WriteAccessAvailable = true;
                MainFormInstance.ProcessName_Text.Enabled = false;
                Report($"Process found! pid {MemLib.ProcessId} (read/write)");
                PlayBeepSound();
            }
            else if (MemLib.OpenByName(process, writeAccess: false))
            {
                ProcessFound = true;
                WriteAccessAvailable = false;
                MainFormInstance.ProcessName_Text.Enabled = false;
                Report($"Process found pid {MemLib.ProcessId}, READ-ONLY - "
                       + "camera can be located but not modified "
                       + $"(write refused: {(MemLib.LastOpenError == 5 ? "access denied" : "err " + MemLib.LastOpenError)})");
                PlayBeepSound();
            }
            else
            {
                // Previously this returned silently, so the label sat on its initial text
                // forever and the tool looked hung. Always say why.
                Report($"Waiting for '{process}': {MemLib.LastLookupNote} "
                       + "- if the process list looks too short, type the game's PID number instead");
            }
        }

        /// <summary>Attaches to an explicit pid, preferring read/write and falling back to read-only.</summary>
        private static bool TryAttachPid(int pid, out string message)
        {
            if (MemLib.Open(pid, writeAccess: true))
            {
                WriteAccessAvailable = true;
                message = $"Process found! pid {pid} (read/write)";
                return true;
            }
            if (MemLib.Open(pid, writeAccess: false))
            {
                WriteAccessAvailable = false;
                message = $"Process found pid {pid}, READ-ONLY - camera can be located but "
                          + "not modified (write refused; run as administrator)";
                return true;
            }
            message = MemLib.LastOpenError == 5
                ? "OpenProcess denied (err 5) - run the tool as administrator"
                : $"OpenProcess failed (err {MemLib.LastOpenError})";
            return false;
        }

        /// <summary>
        /// Updates the status label and appends the same line to d2rcamera.log next to the
        /// executable, so a failure can be reported without reading the UI over someone's
        /// shoulder.
        /// </summary>
        private static void Report(string message)
        {
            try
            {
                MainFormInstance.Status_Label.Text = message;
            }
            catch { /* the label is best-effort */ }

            LogLine(message);
        }

        /// <summary>Appends a line to d2rcamera.log without touching the status label.</summary>
        internal static void LogLine(string message)
        {
            try
            {
                string log = Path.Combine(AppContext.BaseDirectory, "d2rcamera.log");
                File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { /* logging must never take the app down */ }
        }

        public static async void ValueScan_Tick()
        {
            if (!ProgramEnabled || ValuesFound || IsAngleScanning || !ProcessFound) return;
            IsAngleScanning = true;

            var log = new StringBuilder();
            Report("Searching for camera values...");

            LocateResult res;
            try
            {
                res = await Task.Run(() => CameraLocator.Locate(
                    MemLib, s => log.AppendLine(s), CancellationToken.None));
            }
            catch (Exception ex)
            {
                Report("Scan failed: " + ex.Message);
                IsAngleScanning = false;
                return;
            }
            finally
            {
                LastScanLog = log.ToString();
                try
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "d2rcamera.log"),
                        log.ToString());
                }
                catch { /* ignore */ }
            }

            if (res.Found && res.Best is not null)
            {
                ValuesFound = true;
                PlayBeepSound();
                RefreshValues((long)res.Best.Base);

                // The camera exists at more than one address (originally observed 0x170 apart,
                // i.e. a current and a desired copy). If we write only the copy that happened to
                // rank first, the engine can rebuild it from the other one - which is consistent
                // with the reported behaviour: the view follows while we keep writing, then
                // returns to its old position once we stop. Keeping every byte-exact copy in
                // sync removes that possibility.
                CameraMirrors.Clear();
                foreach (var c in res.Candidates)
                {
                    if (c.LegacyExactFields < 4) continue;
                    if ((long)c.Base == (long)res.Best.Base) continue;
                    CameraMirrors.Add((long)c.Base);
                }

                UnlockValueControls();
                Report($"Found via {res.Method} at 0x{res.Best.Base:X} "
                       + $"(legacy {res.Best.LegacyExactFields}/4, churn {res.Best.Churn})"
                       + (CameraMirrors.Count > 0
                          ? $"; mirrors: {string.Join(", ", CameraMirrors.Select(m => "0x" + m.ToString("X")))}"
                          : ""));
            }
            else
            {
                Report("Camera values not found - enter a level and try again");
            }

            IsAngleScanning = false;
        }

        /// <summary>
        /// Zoom keeps its original signature, but expressed as pattern+mask so the
        /// wildcard bytes are explicit rather than a string Memory.dll had to parse.
        /// </summary>
        private static readonly byte[] ZoomPattern =
        {
            0x00,0x00,0x06,0x00,0x00,0x00, 0,0,0,0,0,0,0, 0x43, 0,0,0,0,0,0,0,0,
            0x00,0x00,0x00,0x00, 0x01,0x00,0x00,0x00, 0x00,0x00,0x00,0x00, 0x00,0x00,0x80,0x3F
        };

        private static readonly byte[] ZoomMask = BuildZoomMask();

        private static byte[] BuildZoomMask()
        {
            var m = new byte[ZoomPattern.Length];
            Array.Fill(m, (byte)0xFF);
            for (int i = 6; i < 13; i++) m[i] = 0x00;
            for (int i = 14; i < 22; i++) m[i] = 0x00;
            return m;
        }

        public static async void ZoomScan_Tick()
        {
            if (!ProgramEnabled || ZoomFound || !ValuesFound || IsZoomScanning || !ProcessFound) return;
            IsZoomScanning = true;

            Report("Searching for zoom value...");
            ulong hit = 0;
            try
            {
                hit = await Task.Run(() =>
                {
                    var all = MemLib.FindAll(ZoomPattern, ZoomMask);
                    return all.Count > 0 ? all[0] : 0UL;
                });
            }
            catch (Exception ex)
            {
                Report("Zoom scan failed: " + ex.Message);
                IsZoomScanning = false;
                return;
            }

            if (hit != 0)
            {
                ulong zoomBase = hit + 0x32;
                ZoomValue.UpdateBase((long)zoomBase);
                ZoomFound = true;
                PlayBeepSound();
                Report("Zoom found at: 0x" + zoomBase.ToString("X"));
                UnlockZoomControls();
                MainFormInstance.Zoom_UpDown.Value = (decimal)ZoomValue.Read();
            }
            else
            {
                Report("Zoom value not found");
            }

            IsZoomScanning = false;
        }

        /// <summary>
        /// Other addresses that matched the legacy signature byte for byte. Written alongside
        /// the ranked best so the engine cannot restore a stale copy over our changes.
        /// </summary>
        public static readonly List<long> CameraMirrors = new();

        /// <summary>
        /// Memory sampler used to hunt for the character's heading. See CameraSampler.
        /// </summary>
        public static readonly CameraSampler Sampler = new();

        /// <summary>
        /// When true, every key the global hook sees is logged with its virtual-key code.
        ///
        /// Exists because a laptop F-key is often a multi-function key: pressing F9 alone can
        /// send a media or system code rather than VK_F9, in which case a hotkey bound to F9
        /// simply never matches. Echoing what actually arrives settles it instead of guessing.
        /// </summary>
        public static bool KeyEcho;

        public static void RefreshValues(long valuesBase)
        {
            if (valuesBase <= 0) return;

            // The base address moved, so any mirror list from a previous locate is stale.
            CameraMirrors.Clear();

            PitchValue.UpdateBase(valuesBase);
            CamHeightValue.UpdateBase(valuesBase + 0x4);
            CamPanValue.UpdateBase(valuesBase + 0x8);
            RollValue.UpdateBase(valuesBase - 0x4);

            MainFormInstance.PitchSpinner.Value = (decimal)PitchValue.Read();
            MainFormInstance.CamHeight_UpDown.Value = (decimal)CamHeightValue.Read();
            MainFormInstance.CamPan_UpDown.Value = (decimal)CamPanValue.Read();
            MainFormInstance.Roll_UpDown.Value = (decimal)RollValue.Read();
        }

        private static void UnlockValueControls(bool unlocked = true)
        {
            MainFormInstance.PitchAngle_CheckBox.Enabled = unlocked;
            MainFormInstance.PitchSpinner.Enabled = unlocked;
            MainFormInstance.PitchReset.Enabled = unlocked;
            MainFormInstance.PitchDragButton.Enabled = unlocked;

            MainFormInstance.CamHeight_CheckBox.Enabled = unlocked;
            MainFormInstance.CamHeight_UpDown.Enabled = unlocked;
            MainFormInstance.CamHeight_Reset.Enabled = unlocked;

            MainFormInstance.CamPan_CheckBox.Enabled = unlocked;
            MainFormInstance.CamPan_UpDown.Enabled = unlocked;
            MainFormInstance.CamPan_Reset.Enabled = unlocked;

            MainFormInstance.Roll_CheckBox.Enabled = unlocked;
            MainFormInstance.Roll_UpDown.Enabled = unlocked;
            MainFormInstance.RollReset_Button.Enabled = unlocked;
        }

        private static void UnlockZoomControls(bool unlocked = true)
        {
            MainFormInstance.Zoom_CheckBox.Enabled = unlocked;
            MainFormInstance.Zoom_UpDown.Enabled = unlocked;
            MainFormInstance.ZoomReset_Button.Enabled = unlocked;
            MainFormInstance.ZoomDrag_Button.Enabled = unlocked;
        }

        public static void OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo psi = new() { FileName = url, UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening URL: " + ex.Message);
            }
        }

        public static void PlayBeepSound() => System.Media.SystemSounds.Beep.Play();

        public static string FloatToHex(float floatValue)
        {
            byte[] bytes = BitConverter.GetBytes(floatValue);
            return BitConverter.ToString(bytes).Replace("-", " ");
        }

        public static bool TryChangeValue(FloatValue change, float newValue, NumericUpDown? spinner = null)
        {
            if (change == null || !ProgramEnabled || !ValuesFound || !ProcessFound) return false;
            if (!WriteAccessAvailable)
            {
                Report("Attached read-only - cannot modify. Run the tool as administrator.");
                return false;
            }
            return change.Write(newValue);
        }

        public static bool TryChangeZoom(float zoom, NumericUpDown? spinner = null)
        {
            if (!ProgramEnabled || ZoomValue == null || !ZoomFound || !ProcessFound) return false;
            if (!WriteAccessAvailable)
            {
                Report("Attached read-only - cannot modify zoom. Run the tool as administrator.");
                return false;
            }

            if (ZoomValue.Write(zoom))
            {
                return true;
            }

            ZoomFound = false;
            ZoomValue.UpdateBase(0);
            UnlockZoomControls(false);
            return false;
        }

        public static void TryResetValue(FloatValue change, NumericUpDown? spinner = null)
        {
            if (change == null || change.BaseAddress <= 0 || !ProgramEnabled
                || !ValuesFound || !ProcessFound) return;

            change.Reset();

            if (spinner != null)
            {
                spinner.Value = (decimal)change.Read();
            }
        }
    }
}
