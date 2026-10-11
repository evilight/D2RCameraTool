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

    public int Score;
    public string Note = string.Empty;

    public float this[int field] => field switch
    {
        0 => Roll,
        1 => Pitch,
        2 => Height,
        _ => Yaw,
    };
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
/// The original tool hard-coded one 12-byte AOB signature and broke the moment the
/// game updated. This locator tries progressively weaker strategies:
///
///   A. the original exact signature (works whenever the camera constants are unchanged
///      - they live in the encrypted .text at rest, so this can only be checked live)
///   B. a structural scan for camera-shaped float quads
///   C. a temporal filter: camera ANGLES stay constant while the player moves
///   D. ranking by legacy-value match and by neighbourhood churn (a live game struct
///      churns; a stale copy in a heap does not)
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
    private const int ChurnWindow = 0x200;
    private const int ChurnProbeCount = 96;

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
        foreach (var hit in mem.FindPattern(LegacyAnglePattern, null, regions))
        {
            var c = ReadQuad(mem, hit);
            if (c is null) continue;
            c.LegacyExactFields = CountLegacyMatches(c, exactBytes: true, mem, hit);
            c.Note = "stage A (exact signature)";
            result.Candidates.Add(c);
        }
        log($"  stage A candidates: {result.Candidates.Count}");

        if (result.Candidates.Count > 0)
        {
            result.Method = "legacy signature";
        }
        else
        {
            // ------------------------------------------------------ Stage B
            log("stage B: structural scan for camera-shaped quads ...");
            ct.ThrowIfCancellationRequested();
            result.Candidates = StructuralScan(mem, regions, log, ct);
            log($"  stage B candidates: {result.Candidates.Count}");
            result.Method = "structural";
        }

        if (result.Candidates.Count == 0)
        {
            result.Diagnostics = "no candidates from any stage";
            return result;
        }

        // ---------------------------------------------------------- Stage C
        log("stage C: temporal stability filter (angles are constant while moving) ...");
        ct.ThrowIfCancellationRequested();
        var snapshots = new Dictionary<ulong, byte[]>();
        foreach (var c in result.Candidates)
        {
            var b = mem.ReadBytes(c.Base - 4, 16);
            if (b is { Length: 16 }) snapshots[c.Base] = b;
        }
        log($"  sampling {snapshots.Count} blocks, hold still ~2s ...");
        Thread.Sleep(2000);
        var stable = new List<CameraCandidate>();
        foreach (var c in result.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!snapshots.TryGetValue(c.Base, out var before)) continue;
            var after = mem.ReadBytes(c.Base - 4, 16);
            if (after is { Length: 16 } && after.AsSpan().SequenceEqual(before))
                stable.Add(c);
        }
        log($"  stable: {stable.Count} (dropped {result.Candidates.Count - stable.Count})");
        if (stable.Count > 0) result.Candidates = stable;

        // ---------------------------------------------------------- Stage D
        log("stage D: ranking ...");
        foreach (var c in result.Candidates)
        {
            c.LegacyExactFields = CountLegacyMatches(c, exactBytes: true, mem, c.Base);
            c.Score = c.LegacyExactFields * 1000;
            c.Score -= RoundnessPenalty(c.Roll) + RoundnessPenalty(c.Pitch)
                     + RoundnessPenalty(c.Height) + RoundnessPenalty(c.Yaw);
        }

        // Probe churn on the best candidates, and - when nothing matches the legacy
        // values - on an even sample across the whole list, because with no legacy
        // anchor the scan order is meaningless and only churn discriminates.
        int legacyMax = result.Candidates.Max(c => c.LegacyExactFields);
        List<CameraCandidate> probe;
        if (legacyMax > 0)
        {
            probe = result.Candidates.OrderByDescending(c => c.Score)
                                     .Take(ChurnProbeCount).ToList();
        }
        else
        {
            probe = EvenSample(result.Candidates, ChurnProbeCount * 4);
            log($"  no legacy match; probing churn across {probe.Count} sampled candidates");
        }

        if (probe.Count > 1)
        {
            log($"  measuring neighbourhood churn on {probe.Count} candidates ...");
            MeasureChurnBatch(mem, probe, ct);
        }

        foreach (var c in result.Candidates)
        {
            if (c.Churn > 0) c.Score += Math.Min(c.Churn, 500);
            c.Score += c.Base % 16 == 0 ? 5 : 0;
        }

        result.Candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        result.Best = result.Candidates[0];
        var best = result.Best;
        log($"  best: 0x{best.Base:X}  roll={best.Roll:F6} pitch={best.Pitch:F6} "
            + $"height={best.Height:F6} yaw={best.Yaw:F6}  "
            + $"(legacy {best.LegacyExactFields}/4, churn {best.Churn})");

        result.Found = true;
        return result;
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

    private static int CountLegacyMatches(CameraCandidate c, bool exactBytes,
                                          Win32Memory mem, ulong pitchAddr)
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
    /// Churn for many candidates with a single sleep. Sleeping per candidate made a
    /// few hundred probes take minutes; batching makes it one interval total.
    /// </summary>
    private static void MeasureChurnBatch(Win32Memory mem, List<CameraCandidate> probe,
                                          CancellationToken ct)
    {
        var before = new Dictionary<ulong, byte[]>();
        foreach (var c in probe)
        {
            ulong start = c.Base >= (ulong)ChurnWindow ? c.Base - (ulong)ChurnWindow : 0;
            var b = mem.ReadBytes(start, ChurnWindow * 2);
            if (b is not null) before[c.Base] = b;
        }

        Thread.Sleep(600);
        ct.ThrowIfCancellationRequested();

        foreach (var c in probe)
        {
            if (!before.TryGetValue(c.Base, out var a)) { c.Churn = -1; continue; }
            ulong start = c.Base >= (ulong)ChurnWindow ? c.Base - (ulong)ChurnWindow : 0;
            var b = mem.ReadBytes(start, ChurnWindow * 2);
            if (b is null || b.Length != a.Length) { c.Churn = -1; continue; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
            c.Churn = diff;
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

    private static int MeasureChurn(Win32Memory mem, CameraCandidate c, CancellationToken ct)
    {
        ulong start = c.Base >= (ulong)ChurnWindow ? c.Base - (ulong)ChurnWindow : 0;
        var a = mem.ReadBytes(start, ChurnWindow * 2);
        if (a is null) return -1;
        Thread.Sleep(250);
        ct.ThrowIfCancellationRequested();
        var b = mem.ReadBytes(start, ChurnWindow * 2);
        if (b is null || b.Length != a.Length) return -1;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diff++;
        return diff;
    }

    private static bool IsCameraShaped(float roll, float pitch, float height, float yaw)
    {
        if (!float.IsFinite(roll) || !float.IsFinite(pitch)
            || !float.IsFinite(height) || !float.IsFinite(yaw)) return false;
        if (pitch < -1.45f || pitch > -0.35f) return false;
        if (MathF.Abs(roll) > 0.60f) return false;
        if (height < -0.10f || height > 1.50f) return false;
        if (MathF.Abs(yaw) > 1.40f) return false;
        return true;
    }

    /// <summary>
    /// Penalty for values that look like table constants rather than measured camera
    /// parameters. The real fields have full mantissas; round values (0, 0.5, 1.0,
    /// powers of two, few-significant-bit fractions) dominate false positives.
    /// </summary>
    private static int RoundnessPenalty(float v)
    {
        if (v == 0f) return 40;
        uint mantissa = (uint)BitConverter.SingleToInt32Bits(MathF.Abs(v)) & 0x7FFFFFu;
        if (mantissa == 0) return 40;              // power of two
        if ((mantissa & 0x7FFFFu) == 0) return 20; // ~4 significant bits
        if ((mantissa & 0x7FFFu) == 0) return 8;
        if ((mantissa & 0x1FFu) == 0) return 3;
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
        int scanned = 0;

        foreach (var region in regions)
        {
            // The camera struct lives in writable memory: heap (private) or a
            // writable image section. Read-only image pages are static data and were
            // the main source of false positives in the offline analysis.
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
                var f = new float[floatCount];
                Buffer.BlockCopy(blob, 0, f, 0, floatCount * 4);
                scanned += floatCount;

                int firstIndex = Math.Max(1, pre / 4);
                int lastIndex = Math.Min((pre + n) / 4, floatCount - 3) - 1;
                for (int i = firstIndex; i <= lastIndex; i++)
                {
                    float roll = f[i - 1], pitch = f[i], height = f[i + 1], yaw = f[i + 2];
                    if (!IsCameraShaped(roll, pitch, height, yaw)) continue;
                    if (LooksLikeRamp(f.AsSpan(Math.Max(0, i - 4), Math.Min(9, floatCount - Math.Max(0, i - 4)))))
                        continue;

                    ulong addr = region.Base + off - (ulong)pre + (ulong)(i * 4);
                    found.Add(new CameraCandidate
                    {
                        Base = addr, Roll = roll, Pitch = pitch, Height = height, Yaw = yaw,
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
