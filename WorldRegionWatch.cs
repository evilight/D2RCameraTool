namespace D2RCameraTool;

/// <summary>
/// Two-phase comparison of the memory regions that hold the character's world position.
///
/// The full-process scan finds where the position lives and merges the hits into a handful of
/// regions - only eight of them on the live game, about 32 KB in total. That is small enough to
/// re-read cheaply, which is what makes a reversal test practical here: arm on the first press,
/// turn the character around, press again, and anything that changed by about pi is a heading.
///
/// This exists because a single snapshot cannot identify a heading. The candidate found by
/// inspection had a clean sine/cosine pair next to the position, which is suggestive but proves
/// nothing - plenty of things in a level have an orientation. Only reversal distinguishes the
/// character's facing from a path node's or another entity's.
/// </summary>
internal static class WorldRegionWatch
{
    private static readonly object Gate = new();
    private static List<(long start, int len)> _regions = new();
    private static Dictionary<long, uint> _baseline = new();
    private static long _baseAddr;
    private static int _phase;

    public static bool Armed { get { lock (Gate) return _regions.Count > 0; } }

    /// <summary>
    /// Remembers the current contents of <paramref name="regions"/> as the baseline. Called with
    /// the region list the world-position scan just produced.
    /// </summary>
    public static void Arm(List<(long start, int len)> regions, long baseAddr)
    {
        var words = new Dictionary<long, uint>();
        long total = 0;

        foreach (var (start, len) in regions)
        {
            var bytes = Program.MemLib.ReadBytes((ulong)start, len);
            if (bytes is null) continue;
            total += bytes.Length;
            for (int off = 0; off + 4 <= bytes.Length; off += 4)
                words[start + off] = BitConverter.ToUInt32(bytes, off);
        }

        lock (Gate)
        {
            _regions = new List<(long, int)>(regions);
            _baseline = words;
            _baseAddr = baseAddr;
            _phase = 1;
        }

        Program.LogLine($"WATCH armed on {regions.Count} region(s), {total / 1024} KB. "
                        + "Turn the character around, then run the scan again to diff.");
    }

    /// <summary>
    /// Re-reads the armed regions and reports what changed. Returns false when nothing is armed.
    /// </summary>
    public static bool DiffAndReport()
    {
        List<(long start, int len)> regions;
        Dictionary<long, uint> baseline;
        long baseAddr;
        int phase;

        lock (Gate)
        {
            if (_regions.Count == 0) return false;
            regions = new List<(long, int)>(_regions);
            baseline = new Dictionary<long, uint>(_baseline);
            baseAddr = _baseAddr;
            phase = ++_phase;
        }

        var now = new Dictionary<long, uint>();
        int read = 0;
        foreach (var (start, len) in regions)
        {
            var bytes = Program.MemLib.ReadBytes((ulong)start, len);
            if (bytes is null) continue;
            read += bytes.Length;
            for (int off = 0; off + 4 <= bytes.Length; off += 4)
                now[start + off] = BitConverter.ToUInt32(bytes, off);
        }

        Program.LogLine(CameraSampler.BuildDiffReport(
            baseline, now, baseAddr,
            $"world regions phase 1 -> {phase} ({regions.Count} region(s), {read / 1024} KB)",
            "if the character was turned around, a heading shows up as near-pi, negated, "
            + "or a half-range integer"));

        return true;
    }

    public static void Disarm()
    {
        lock (Gate)
        {
            _regions.Clear();
            _baseline.Clear();
            _phase = 0;
        }
    }
}
