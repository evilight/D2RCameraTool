using System.Buffers.Binary;

namespace D2RCameraTool;

internal sealed class CameraCandidate
{
    /// <summary>Address of the pitch field; roll/height/yaw sit at -4/+4/+8.</summary>
    public ulong Base;

    public float Roll, Pitch, Height, Yaw;

    /// <summary>How many of the four fields are byte-identical to the 1.6-era defaults.</summary>
    public int LegacyExactFields;

    /// <summary>Bytes that changed inside a +-512 byte window during the sample interval.</summary>
    public int Churn = -1;

    /// <summary>Churn re-measured over a wider window for the leading candidates.</summary>
    public int ChurnWide = -1;

    public int Score;
    public string Note = string.Empty;
}

internal sealed class LocateResult
{
    public bool Found;
    public string Method = "none";
    public CameraCandidate? Best;
    public List<CameraCandidate> Candidates = new();
    public string Diagnostics = string.Empty;
}

/// <summary>
/// Version-independent camera locator.
///
/// The original tool hard-coded one 12-byte AOB signature and broke the moment the game
/// updated. This locator tries progressively weaker strategies:
///
///   A. the original exact signature
///   B. a structural scan for camera-shaped float quads
///   C. a temporal filter: camera ANGLES stay constant while the player moves
///   D. ranking by legacy-value match and by neighbourhood churn
///
/// Stage A does NOT short-circuit. If the game changed the camera constants, stale copies
/// of the OLD constants can still be sitting in memory; a locator that trusts the signature
/// unconditionally would confidently return one of those dead copies. So stage A's results
/// are liveness-checked, and when every one of them is frozen the locator escalates to the
/// structural scan and stops treating the legacy match as evidence.
/// </summary>
internal static class CameraLocator
{
    // roll | pitch | height | yaw, exactly as the original tool's FloatValue defaults.
    public static readonly byte[] DefaultRollBytes = { 0x81, 0xD8, 0xCA, 0x3D };
    public static readonly byte[] DefaultPitchBytes = { 0x44, 0x74, 0x64, 0xBF };
    public static readonly byte[] DefaultHeightBytes = { 0x3F, 0xDB, 0x74, 0x3E };
    public static readonly byte[] DefaultYawBytes = { 0xF4, 0x41, 0xBD, 0x3E };

    /// <summary>pitch|height|yaw, the sequence the original tool scanned for.</summary>
    public static readonly byte[] LegacyAnglePattern = Concat(
        DefaultPitchBytes, DefaultHeightBytes, DefaultYawBytes);

    public static readonly float DefaultRoll = BitConverter.ToSingle(DefaultRollBytes, 0);
    public static readonly float DefaultPitch = BitConverter.ToSingle(DefaultPitchBytes, 0);
    public static readonly float DefaultHeight = BitConverter.ToSingle(DefaultHeightBytes, 0);
    public static readonly float DefaultYaw = BitConverter.ToSingle(DefaultYawBytes, 0);

    private const int MaxStructuralCandidates = 200_000;

    /// <summary>Wide window used for refinement and for the stage-A liveness check.</summary>
    private const int ChurnWindow = 0x200;

    /// <summary>Narrow window for the bulk pass over every candidate (keeps memory bounded).</summary>
    private const int BulkChurnWindow = 0x80;

    private const int BulkProbeLimit = 100_000;
    private const int RefineCount = 256;
    private const int LivenessProbeLimit = 512;

    private static byte[] Concat(params byte[][] arrays)
    {
        var outBuf = new byte[arrays.Sum(a => a.Length)];
        int o = 0;
        foreach (var a in arrays) { Buffer.BlockCopy(a, 0, outBuf, o, a.Length); o += a.Length; }
        return outBuf;
    }

    // ------------------------------------------------------------------ public

    public static LocateResult Locate(Win32Memory mem, Action<string> log,
                                      CancellationToken ct = default)
    {
        var result = new LocateResult();
        if (!mem.IsOpen)
        {
            result.Diagnostics = "no process open";
            return result;
        }

        var regions = mem.EnumerateRegions();
        long total = regions.Sum(r => (long)r.Size);
        log($"scannable regions: {regions.Count}, {total / 1e6:F1} MB");

        // ---------------------------------------------------------- Stage A
        log("stage A: legacy 12-byte signature ...");
        ct.ThrowIfCancellationRequested();
        var cands = ScanLegacy(mem, regions);
        log($"  stage A candidates: {cands.Count}");

        string method = cands.Count > 0 ? "legacy signature" : "structural";
        bool legacyTrusted = cands.Count > 0;
        bool needStructural = cands.Count == 0;

        if (!needStructural)
        {
            log("  liveness check on stage A candidates ...");
            if (AnyLive(mem, cands, ct))
            {
                log("  at least one stage A candidate churns -> treating the signature as valid");
            }
            else
            {
                log("  every stage A candidate is frozen -> stale copies of old constants;"
                    + " escalating to the structural scan");
                needStructural = true;
                legacyTrusted = false;
            }
        }

        // ---------------------------------------------------------- Stage B
        if (needStructural)
        {
            log("stage B: structural scan for camera-shaped quads ...");
            ct.ThrowIfCancellationRequested();
            var b = StructuralScan(mem, regions, log, ct);
            log($"  stage B candidates: {b.Count}");

            var seen = new HashSet<ulong>(cands.Select(c => c.Base));
            int added = 0;
            foreach (var c in b)
            {
                if (!seen.Add(c.Base)) continue;
                c.Note = "stage B (structural)";
                cands.Add(c);
                added++;
            }
            log($"  merged {added} new candidates (total {cands.Count})");

            method = legacyTrusted ? "legacy+structural" : "structural";
        }

        if (cands.Count == 0)
        {
            result.Diagnostics = "no candidates from any stage";
            return result;
        }

        // ---------------------------------------------------------- Stage C
        log("stage C: temporal stability filter (angles are constant while moving) ...");
        ct.ThrowIfCancellationRequested();
        var snapshots = new Dictionary<ulong, byte[]>();
        foreach (var c in cands)
        {
            var b = mem.ReadBytes(c.Base - 4, 16);
            if (b is { Length: 16 }) snapshots[c.Base] = b;
        }
        log($"  sampling {snapshots.Count} blocks, hold still ~2s ...");
        Thread.Sleep(2000);
        var stable = new List<CameraCandidate>();
        foreach (var c in cands)
        {
            ct.ThrowIfCancellationRequested();
            if (!snapshots.TryGetValue(c.Base, out var before)) continue;
            var after = mem.ReadBytes(c.Base - 4, 16);
            if (after is { Length: 16 } && after.AsSpan().SequenceEqual(before))
                stable.Add(c);
        }
        log($"  stable: {stable.Count} (dropped {cands.Count - stable.Count})");
        if (stable.Count > 0) cands = stable;

        // ---------------------------------------------------------- Stage D
        log(legacyTrusted
            ? "stage D: ranking by legacy match, churn as tie-break ..."
            : "stage D: ranking by churn (legacy match untrusted after escalation) ...");

        foreach (var c in cands)
        {
            c.LegacyExactFields = CountLegacyMatches(mem, c.Base);
            int legacy = legacyTrusted ? c.LegacyExactFields * 1000 : 0;
            c.Score = legacy
                      - RoundnessPenalty(c.Roll) - RoundnessPenalty(c.Pitch)
                      - RoundnessPenalty(c.Height) - RoundnessPenalty(c.Yaw);
        }

        // Probe churn on EVERY candidate. Sampling was a real bug: with no legacy anchor
        // the true camera is one arbitrary entry among thousands, and an even sample
        // simply missed it, leaving it indistinguishable from a probed-but-frozen decoy.
        List<CameraCandidate> probe = cands.Count > BulkProbeLimit
            ? EvenSample(cands, BulkProbeLimit)
            : cands;
        log($"  probing churn on {probe.Count} candidates ...");
        ChurnPass(mem, probe, BulkChurnWindow, (c, v) => c.Churn = v, ct);

        foreach (var c in cands)
        {
            // Churn is the strongest liveness signal available; cap it so it cannot
            // swamp a trustworthy legacy match. Unprobed (-1) must never outrank a
            // candidate that was actually observed.
            int churn = c.Churn < 0 ? 0 : Math.Min(c.Churn, 500);
            c.Score += legacyTrusted ? churn : churn * 20;
            if (c.Churn < 0) c.Score -= 100;
            if (c.Base % 16 == 0) c.Score += 5;
        }

        if (!legacyTrusted)
        {
            // No legacy anchor: widen the window on the leaders so churn magnitude,
            // not the arbitrary scan order, decides.
            var leaders = cands.OrderByDescending(x => x.Score).Take(RefineCount).ToList();
            log($"  refining churn over +-0x{ChurnWindow:X} on top {leaders.Count} ...");
            ChurnPass(mem, leaders, ChurnWindow, (c, v) => c.ChurnWide = v, ct);
            foreach (var c in leaders)
                if (c.ChurnWide > 0) c.Score += Math.Min(c.ChurnWide, 2000);
        }

        cands.Sort((a, b) => b.Score.CompareTo(a.Score));
        result.Candidates = cands;
        result.Best = cands[0];
        result.Method = method;

        var best = result.Best;
        log($"  best: 0x{best.Base:X}  roll={best.Roll:F6} pitch={best.Pitch:F6} "
            + $"height={best.Height:F6} yaw={best.Yaw:F6}  "
            + $"(legacy {best.LegacyExactFields}/4, churn {best.Churn}, score {best.Score})");

        result.Found = true;
        return result;
    }

    // ------------------------------------------------------------------ stages

    private static List<CameraCandidate> ScanLegacy(Win32Memory mem, List<MemoryRegion> regions)
    {
        var found = new List<CameraCandidate>();
        foreach (var hit in mem.FindPattern(LegacyAnglePattern, null, regions))
        {
            var c = ReadQuad(mem, hit);
            if (c is null) continue;
            c.Note = "stage A (exact signature)";
            found.Add(c);
        }
        return found;
    }

    /// <summary>True when any candidate's neighbourhood changed (i.e. it is a live struct).</summary>
    private static bool AnyLive(Win32Memory mem, List<CameraCandidate> cands, CancellationToken ct)
    {
        var before = new List<(ulong start, byte[] bytes)>();
        foreach (var c in cands.Take(LivenessProbeLimit))
        {
            ulong start = c.Base >= (ulong)ChurnWindow ? c.Base - (ulong)ChurnWindow : 0;
            var b = mem.ReadBytes(start, ChurnWindow * 2);
            if (b is not null) before.Add((start, b));
        }
        if (before.Count == 0) return false;

        Thread.Sleep(500);
        ct.ThrowIfCancellationRequested();

        foreach (var (start, a) in before)
        {
            var b = mem.ReadBytes(start, ChurnWindow * 2);
            if (b is null || b.Length != a.Length) continue;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Reads the four consecutive floats with pitch at <paramref name="pitchAddr"/>.</summary>
    public static CameraCandidate? ReadQuad(Win32Memory mem, ulong pitchAddr)
    {
        var b = mem.ReadBytes(pitchAddr - 4, 16);
        if (b is not { Length: 16 }) return null;
        return new CameraCandidate
        {
            Base = pitchAddr,
            Roll = BitConverter.ToSingle(b, 0),
            Pitch = BitConverter.ToSingle(b, 4),
            Height = BitConverter.ToSingle(b, 8),
            Yaw = BitConverter.ToSingle(b, 12),
        };
    }

    private static int CountLegacyMatches(Win32Memory mem, ulong pitchAddr)
    {
        var b = mem.ReadBytes(pitchAddr - 4, 16);
        if (b is not { Length: 16 }) return 0;
        int n = 0;
        if (b.AsSpan(0, 4).SequenceEqual(DefaultRollBytes)) n++;
        if (b.AsSpan(4, 4).SequenceEqual(DefaultPitchBytes)) n++;
        if (b.AsSpan(8, 4).SequenceEqual(DefaultHeightBytes)) n++;
        if (b.AsSpan(12, 4).SequenceEqual(DefaultYawBytes)) n++;
        return n;
    }

    /// <summary>
    /// One churn pass over a candidate list: snapshot all windows, sleep once, re-read and
    /// count differing bytes. Sleeping per candidate made a few hundred probes take
    /// minutes; batching makes the whole pass cost a single interval.
    /// </summary>
    private static void ChurnPass(Win32Memory mem, List<CameraCandidate> list, int window,
                                  Action<CameraCandidate, int> assign, CancellationToken ct)
    {
        if (list.Count == 0) return;

        var before = new Dictionary<ulong, byte[]>();
        foreach (var c in list)
        {
            ulong start = c.Base >= (ulong)window ? c.Base - (ulong)window : 0;
            var b = mem.ReadBytes(start, window * 2);
            if (b is not null) before[c.Base] = b;
            else assign(c, -1);
        }

        Thread.Sleep(600);
        ct.ThrowIfCancellationRequested();

        foreach (var c in list)
        {
            if (!before.TryGetValue(c.Base, out var a)) continue;
            ulong start = c.Base >= (ulong)window ? c.Base - (ulong)window : 0;
            var b = mem.ReadBytes(start, window * 2);
            if (b is null || b.Length != a.Length) { assign(c, -1); continue; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
            assign(c, diff);
        }
    }

    private static List<CameraCandidate> EvenSample(List<CameraCandidate> src, int count)
    {
        if (src.Count <= count) return new List<CameraCandidate>(src);
        var outp = new List<CameraCandidate>(count);
        double step = (double)src.Count / count;
        for (int i = 0; i < count; i++)
        {
            int idx = (int)(i * step);
            if (idx < src.Count) outp.Add(src[idx]);
        }
        return outp;
    }

    /// <summary>
    /// Camera parameters are ordinary measured magnitudes. Denormals and absurd exponents
    /// are the signature of arbitrary bit patterns being read as floats, and they dominated
    /// the false positives: |1e-37| trivially satisfies a "|roll| &lt;= 0.6" range test.
    /// </summary>
    private static bool IsPlausibleMagnitude(float v)
    {
        if (!float.IsFinite(v)) return false;
        if (v == 0f) return true;
        float a = MathF.Abs(v);
        return a >= 1e-4f && a <= 1e4f;
    }

    private static bool IsCameraShaped(float roll, float pitch, float height, float yaw)
    {
        if (!float.IsFinite(roll) || !float.IsFinite(pitch)
            || !float.IsFinite(height) || !float.IsFinite(yaw)) return false;
        if (pitch < -1.45f || pitch > -0.35f) return false;
        if (MathF.Abs(roll) > 0.60f) return false;
        if (height < -0.10f || height > 1.50f) return false;
        if (MathF.Abs(yaw) > 1.40f) return false;
        return IsPlausibleMagnitude(roll) && IsPlausibleMagnitude(height)
               && IsPlausibleMagnitude(yaw);
    }

    /// <summary>
    /// Small penalty for values that look like table constants rather than measured camera
    /// parameters. Deliberately modest: it only breaks ties, because an earlier version used
    /// weights large enough to outweigh the churn signal and that inverted the ranking.
    /// </summary>
    private static int RoundnessPenalty(float v)
    {
        if (v == 0f) return 2;
        uint mantissa = (uint)BitConverter.SingleToInt32Bits(MathF.Abs(v)) & 0x7FFFFFu;
        if (mantissa == 0) return 12;              // power of two
        if ((mantissa & 0x7FFFFu) == 0) return 5;  // ~4 significant bits
        if ((mantissa & 0x7FFFu) == 0) return 2;
        return 0;
    }

    /// <summary>True when the surrounding floats advance by a near-constant step.</summary>
    private static bool LooksLikeRamp(ReadOnlySpan<float> f)
    {
        if (f.Length < 9) return false;
        float d0 = f[1] - f[0];
        if (MathF.Abs(d0) < 1e-6f) return false;
        for (int i = 1; i < f.Length - 1; i++)
        {
            float d = f[i + 1] - f[i];
            if (!float.IsFinite(d) || MathF.Abs(d - d0) > 1e-4f) return false;
        }
        return true;
    }

    private static List<CameraCandidate> StructuralScan(Win32Memory mem,
        List<MemoryRegion> regions, Action<string> log, CancellationToken ct)
    {
        var found = new List<CameraCandidate>();
        bool capped = false;
        long scanned = 0;
        var fbuf = Array.Empty<float>();

        foreach (var region in regions)
        {
            // The camera struct lives in writable memory: heap (private) or a writable
            // image section. Read-only image pages are static data and were the main
            // source of false positives in the offline analysis.
            if (!region.IsPrivate && !region.IsImage) continue;

            ulong off = 0;
            while (off < region.Size)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Win32Memory.ChunkSize, region.Size - off);
                int pre = off >= 4 ? 4 : 0;
                int post = (off + (ulong)n + 8 <= region.Size) ? 8 : 0;
                var blob = mem.ReadBytes(region.Base + off - (ulong)pre, n + pre + post);
                if (blob is null || blob.Length < 24) { off += (ulong)n; continue; }

                int floatCount = blob.Length / 4;
                if (fbuf.Length < floatCount) fbuf = new float[floatCount];
                Buffer.BlockCopy(blob, 0, fbuf, 0, floatCount * 4);
                var f = fbuf.AsSpan(0, floatCount);
                scanned += floatCount;

                int firstIndex = Math.Max(1, pre / 4);
                int lastIndex = Math.Min((pre + n) / 4, floatCount - 3) - 1;
                for (int i = firstIndex; i <= lastIndex; i++)
                {
                    float roll = f[i - 1], pitch = f[i], height = f[i + 1], yaw = f[i + 2];
                    if (!IsCameraShaped(roll, pitch, height, yaw)) continue;

                    int rampStart = Math.Max(0, i - 4);
                    int rampLen = Math.Min(9, floatCount - rampStart);
                    if (rampLen >= 9 && LooksLikeRamp(f.Slice(rampStart, rampLen))) continue;

                    found.Add(new CameraCandidate
                    {
                        Base = region.Base + off - (ulong)pre + (ulong)(i * 4),
                        Roll = roll, Pitch = pitch, Height = height, Yaw = yaw,
                        Note = "stage B (structural)"
                    });
                    if (found.Count >= MaxStructuralCandidates) { capped = true; break; }
                }
                if (capped) break;
                off += (ulong)n;
            }
            if (capped) break;
        }

        log($"  scanned {scanned / 1e6:F1}M float positions"
            + (capped ? $" (capped at {MaxStructuralCandidates})" : string.Empty));
        return found;
    }
}
