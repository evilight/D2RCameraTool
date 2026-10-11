using System.Globalization;
using System.Text;

namespace D2RCameraTool;

/// <summary>
/// Captures labelled snapshots of memory around the located camera so the character's heading
/// can be found by comparison.
///
/// Why this exists: a follow camera needs the camera yaw driven from the character's own facing
/// every frame, and that facing is not located. D2R.exe's .text is encrypted, so it cannot be
/// read out of the code - the only route is behaviour.
///
/// The discriminator is a REVERSAL test. Walk one way, stop, capture; walk the opposite way,
/// stop, capture. The character's heading differs by about pi between those two poses, while
/// position, animation counters and timers do not. So the diff looks for values whose angular
/// change is near pi, and reports them first.
///
/// Captures are triggered by a global hotkey rather than a button on purpose: clicking the tool
/// would take focus away from the game, and losing focus is known to change game state.
/// </summary>
internal sealed class CameraSampler
{
    public Keys HotKey = Keys.F9;

    /// <summary>Bytes examined either side of the camera base.</summary>
    public int WindowRadius = 0x4000;

    public bool RawDump = true;

    /// <summary>Angular change counted as "close to reversed", in radians.</summary>
    private const double NearPiTolerance = 0.45;

    /// <summary>How many non-heading differences to report as context.</summary>
    private const int OtherDiffLimit = 40;

    private const string RawDumpFile = "camera_samples.txt";

    private readonly object _gate = new();
    private Sample? _previous;
    private int _count;
    private string _lastOutcome = "(no capture yet)";

    public string LastOutcome { get { lock (_gate) return _lastOutcome; } }
    public int Count { get { lock (_gate) return _count; } }

    internal sealed class Sample
    {
        public int Index;
        public DateTime At;
        public long Base;
        public float Pitch, Yaw, Roll, Height, Zoom;
        public Dictionary<long, uint> Words = new();
    }

    /// <summary>Captures a sample and diffs it against the previous one. Never throws.</summary>
    public void Capture(string reason)
    {
        try
        {
            CaptureCore(reason);
        }
        catch (Exception ex)
        {
            lock (_gate) _lastOutcome = "capture failed: " + ex.Message;
            Program.LogLine("SAMPLE capture failed: " + ex.Message);
        }
    }

    private void CaptureCore(string reason)
    {
        if (!Program.ValuesFound || Program.CamPanValue.BaseAddress <= 0)
        {
            lock (_gate) _lastOutcome = "not attached to a located camera";
            Program.LogLine("SAMPLE ignored - camera not located yet");
            return;
        }

        // The tool's convention: CamPanValue sits at base + 8, so the base is pitch.
        long baseAddr = Program.PitchValue.BaseAddress;
        if (baseAddr <= 0)
        {
            lock (_gate) _lastOutcome = "no camera base";
            return;
        }

        var s = new Sample
        {
            Index = ++_count,
            At = DateTime.Now,
            Base = baseAddr,
            Pitch = Program.PitchValue.Read(),
            Yaw = Program.CamPanValue.Read(),
            Roll = Program.RollValue.Read(),
            Height = Program.CamHeightValue.Read(),
            Zoom = Program.ZoomValue.Read(),
        };

        int radius = WindowRadius;
        var bytes = Program.MemLib.ReadBytes((ulong)(baseAddr - radius), radius * 2);
        if (bytes is null || bytes.Length < 8)
        {
            lock (_gate) _lastOutcome = "window read failed";
            Program.LogLine("SAMPLE window read failed");
            return;
        }

        for (int off = 0; off + 4 <= bytes.Length; off += 4)
        {
            uint w = BitConverter.ToUInt32(bytes, off);
            if (w == 0) continue;   // most of the window is zero; the offset identifies the gap
            s.Words[off - radius] = w;
        }

        if (RawDump) WriteRaw(s);

        string summary = $"sample #{s.Index} ({reason}) base=0x{baseAddr:X} "
            + $"yaw={s.Yaw:F6} pitch={s.Pitch:F6} roll={s.Roll:F6} height={s.Height:F6} "
            + $"zoom={s.Zoom:F6} nonZeroWords={s.Words.Count}";

        Sample? prev = _previous;
        _previous = s;

        lock (_gate) _lastOutcome = summary;
        Program.LogLine("SAMPLE " + summary);

        if (prev is null)
        {
            Program.LogLine("SAMPLE first capture - take the reversed pose and capture again");
            return;
        }

        if (prev.Base != s.Base)
        {
            Program.LogLine("SAMPLE previous sample used a different camera base "
                            + $"(0x{prev.Base:X} vs 0x{s.Base:X}) - cannot diff, re-locate and redo");
            return;
        }

        ReportDiff(prev, s);
    }

    private static void ReportDiff(Sample a, Sample b) =>
        Program.LogLine(BuildDiffReport(a, b));

    /// <summary>
    /// Compares two samples. Heading candidates come first: a value whose angular change is near
    /// pi is a rotation that reversed, which is exactly what turning the character around does.
    ///
    /// Split out from the logging so it can be exercised without a game attached - see the
    /// --sampler-selftest CLI mode.
    /// </summary>
    internal static string BuildDiffReport(Sample a, Sample b)
    {
        var reversed = new List<string>();
        var others = new List<(double magnitude, string text)>();

        foreach (var kv in b.Words)
        {
            long off = kv.Key;
            uint wb = kv.Value;
            if (!a.Words.TryGetValue(off, out uint wa)) continue;
            if (wa == wb) continue;

            float fa = BitConverter.ToSingle(BitConverter.GetBytes(wa), 0);
            float fb = BitConverter.ToSingle(BitConverter.GetBytes(wb), 0);

            double delta = 0;
            bool angular = false;

            if (IsPlausibleAngle(fa) && IsPlausibleAngle(fb))
            {
                delta = WrapPi(fb - fa);
                angular = Math.Abs(Math.Abs(delta) - Math.PI) <= NearPiTolerance;
            }

            int ia = unchecked((int)wa), ib = unchecked((int)wb);

            if (angular)
            {
                reversed.Add($"    +0x{off:X6} (0x{(long)b.Base + off:X}) "
                             + $"float {fa:F6} -> {fb:F6}  delta={delta:F6} rad "
                             + $"(reversed; as int {ia} -> {ib})");
            }
            else
            {
                double mag = Math.Abs(fb - fa);
                others.Add((mag, $"    +0x{off:X6} (0x{(long)b.Base + off:X}) "
                                 + $"float {fa:F6} -> {fb:F6} (d={fb - fa:F6})  int {ia} -> {ib}"));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"SAMPLE diff #{a.Index} -> #{b.Index}: "
                      + $"{reversed.Count} value(s) changed by about pi, {others.Count} other change(s)");
        sb.AppendLine($"SAMPLE   pose A: yaw={a.Yaw:F6} pitch={a.Pitch:F6} roll={a.Roll:F6} "
                      + $"height={a.Height:F6} zoom={a.Zoom:F6}");
        sb.AppendLine($"SAMPLE   pose B: yaw={b.Yaw:F6} pitch={b.Pitch:F6} roll={b.Roll:F6} "
                      + $"height={b.Height:F6} zoom={b.Zoom:F6}");

        if (reversed.Count > 0)
        {
            sb.AppendLine("SAMPLE candidates whose angle reversed (most likely the heading):");
            foreach (var line in reversed) sb.AppendLine(line);
        }
        else
        {
            sb.AppendLine("SAMPLE no value reversed by about pi in this window - the heading is "
                          + "either outside it or not stored as an angle");
        }

        if (others.Count > 0)
        {
            others.Sort((x, y) => y.magnitude.CompareTo(x.magnitude));
            sb.AppendLine($"SAMPLE largest other changes (context, top {Math.Min(OtherDiffLimit, others.Count)}):");
            for (int i = 0; i < others.Count && i < OtherDiffLimit; i++) sb.AppendLine(others[i].text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// A float worth treating as an angle. Real angles live roughly in [-2pi, 2pi]; values far
    /// outside that are coordinates, counters or bit patterns that happen to look like floats.
    /// </summary>
    private static bool IsPlausibleAngle(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return false;
        float a = Math.Abs(v);
        return a <= (float)(Math.PI * 2 + 0.5);
    }

    private static double WrapPi(double radians)
    {
        const double twoPi = Math.PI * 2;
        while (radians > Math.PI) radians -= twoPi;
        while (radians <= -Math.PI) radians += twoPi;
        return radians;
    }

    private void WriteRaw(Sample s)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, RawDumpFile);
            var sb = new StringBuilder();
            sb.AppendLine($"=== sample #{s.Index}  {s.At:yyyy-MM-dd HH:mm:ss}  base=0x{s.Base:X}  "
                          + $"radius=0x{s.Words.Count switch { 0 => 0, _ => WindowRadius }}");
            sb.AppendLine($"# yaw={s.Yaw.ToString("F6", CultureInfo.InvariantCulture)} "
                          + $"pitch={s.Pitch.ToString("F6", CultureInfo.InvariantCulture)} "
                          + $"roll={s.Roll.ToString("F6", CultureInfo.InvariantCulture)} "
                          + $"height={s.Height.ToString("F6", CultureInfo.InvariantCulture)} "
                          + $"zoom={s.Zoom.ToString("F6", CultureInfo.InvariantCulture)}");
            // offset relative to the camera base : raw 4-byte word. Hex keeps both the float and
            // the integer reading available offline.
            foreach (var kv in s.Words.OrderBy(k => k.Key))
                sb.AppendLine($"{kv.Key:X6} {kv.Value:X8}");
            File.AppendAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            Program.LogLine("SAMPLE raw dump failed: " + ex.Message);
        }
    }
}
