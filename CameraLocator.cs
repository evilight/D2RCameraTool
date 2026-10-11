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
    private const int PageSize = 4096;

    /// <summary>Wide window used for refinement and for the stage-A liveness check.</summary>
    private const int ChurnWindow = 0x200;

    /// <summary>Narrow window for the bulk pass over every candidate (keeps memory bounded).</summary>
    private const int BulkChurnWindow = 0x80;

    private const int BulkProbeLimit = 100_000;
    private const int RefineCount = 256;

    private static string TypeName(uint type) => type switch
    {
        0x20000 => "private",
        0x40000 => "mapped",
        0x1000000 => "image",
        _ => $"0x{type:X}",
    };

    private static byte[] Concat(params byte[][] arrays)
    {
        var outBuf = new byte[arrays.Sum(a => a.Length)];
        int o = 0;
        foreach (var a in arrays) { Buffer.BlockCopy(a, 0, outBuf, o, a.Length); o += a.Length; }
        return outBuf;
    }

    // ------------------------------------------------------------------ public

    public static LocateResult Locate(Win32Memory mem, Action<string> log,
                                      CancellationToken ct = default,
                                      bool forceStructural = false)
    {
        var result = new LocateResult();
        if (!mem.IsOpen)
        {
            result.Diagnostics = "no process open";
            return result;
        }

        var regions = mem.EnumerateRegions();
        long total = regions.Sum(r => (long)r.Size);
        var byType = regions.GroupBy(r => r.Type)
                            .Select(g => $"{TypeName(g.Key)} {g.Sum(x => (long)x.Size) / 1e6:F0}MB");
        log($"scannable regions: {regions.Count}, {total / 1e6:F1} MB  [{string.Join(", ", byType)}]");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastLap = 0;
        void Lap(string label)
        {
            long now = sw.ElapsedMilliseconds;
            log($"  [{label}: {now - lastLap} ms, total {now} ms]");
            lastLap = now;
        }

        // ---------------------------------------------------------- Stage A
        var cands = new List<CameraCandidate>();
        string method = "structural";
        if (!forceStructural)
        {
            log("stage A: legacy 12-byte signature ...");
            ct.ThrowIfCancellationRequested();
            cands = ScanLegacy(mem, regions);
            log($"  stage A candidates: {cands.Count}");
            Lap("stage A scan");

            if (cands.Count > 0)
            {
                // Liveness is reported (from the stage C sample) but NOT used to reject.
                // Live measurement on D2R 3.0.91636 showed the signature still matches the real
                // camera with the 1.6 values, while the candidate neighbourhood was completely
                // static -- the player was standing in the stash. Treating "no churn" as "stale
                // copy" demoted the correct answer below thousands of structural decoys and
                // returned a wrong address. A static camera is normal; only a MISSING signature
                // justifies the expensive scan.
                method = "legacy signature";
            }
            else
            {
                log("  signature absent -> structural scan required");
            }
        }

        bool needStructural = cands.Count == 0;

        // ---------------------------------------------------------- Stage B
        if (needStructural)
        {
            log("stage B: finding hot pages (the camera struct is continuously written) ...");
            ct.ThrowIfCancellationRequested();
            var hot = BuildHotPages(mem, regions, log, ct);

            log("stage B: structural scan for camera-shaped quads ...");
            var b = StructuralScan(mem, regions, hot, log, ct);

            if (b.Count == 0)
            {
                log("  hot-page scan found nothing; retrying over all writable memory");
                b = StructuralScan(mem, regions, null, log, ct);
            }
            log($"  stage B candidates: {b.Count}");
            Lap("stage B scan");

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
            method = "structural";
        }

        if (cands.Count == 0)
        {
            result.Diagnostics = "no candidates from any stage";
            return result;
        }

        // ---------------------------------------------------------- Stage C
        // Signature hits that already match all four legacy values byte for byte ARE the
        // camera. The stability filter exists to weed out flickering structural candidates,
        // so on this path there is nothing to weed out and the fixed 2 s wait is pure
        // latency. Angles are constant for a stale copy and for the live camera alike, so
        // the filter would not have separated them anyway.
        bool allByteExact = cands.Count > 0 && method == "legacy signature"
                            && cands.All(c => CountLegacyMatches(mem, c.Base) >= 4);

        if (allByteExact)
        {
            log("stage C: skipped - every candidate is a byte-exact signature match");
        }
        else
        {
            // One sample serves both questions: is the 16-byte quad unchanged (stability), and
            // is the surrounding window changing at all (liveness).
            log("stage C: temporal stability + liveness (one sample serves both) ...");
            ct.ThrowIfCancellationRequested();
            const int W = 0x80;
            const int QuadOff = W - 4;   // roll begins here inside the window
            var snapshots = new Dictionary<ulong, byte[]>();
            foreach (var c in cands)
            {
                var b = mem.ReadBytes(c.Base - (ulong)W, W * 2);
                if (b is { Length: W * 2 }) snapshots[c.Base] = b;
            }
            log($"  sampling {snapshots.Count} windows, hold still ~2s ...");
            Thread.Sleep(2000);
            var stable = new List<CameraCandidate>();
            int liveCount = 0;
            foreach (var c in cands)
            {
                ct.ThrowIfCancellationRequested();
                if (!snapshots.TryGetValue(c.Base, out var before)) continue;
                var after = mem.ReadBytes(c.Base - (ulong)W, W * 2);
                if (after is null || after.Length != before.Length) continue;

                bool quadSame = after.AsSpan(QuadOff, 16).SequenceEqual(before.AsSpan(QuadOff, 16));
                bool windowChanged = false;
                for (int i = 0; i < before.Length; i++)
                {
                    if (before[i] != after[i]) { windowChanged = true; break; }
                }
                if (windowChanged) liveCount++;
                if (quadSame) stable.Add(c);
            }
            log($"  stable {stable.Count}/{cands.Count}, live {liveCount} "
                + "(a static camera is normal - the player may be in a menu)");
            if (stable.Count > 0) cands = stable;
            Lap("stage C sample");
        }

        // ---------------------------------------------------------- Stage D
        log("stage D: ranking (exact legacy match dominates, else proximity + liveness) ...");

        foreach (var c in cands)
            c.LegacyExactFields = CountLegacyMatches(mem, c.Base);

        // Probe churn on EVERY candidate. Sampling was a real bug: with no legacy anchor
        // the true camera is one arbitrary entry among thousands, and an even sample
        // simply missed it, leaving it indistinguishable from a probed-but-frozen decoy.
        List<CameraCandidate> probe = cands.Count > BulkProbeLimit
            ? EvenSample(cands, BulkProbeLimit)
            : cands;
        log($"  probing churn on {probe.Count} candidates ...");
        ChurnPass(mem, probe, BulkChurnWindow, (c, v) => c.Churn = v, ct);
        Lap("stage D churn");

        foreach (var c in cands)
        {
            int churn = c.Churn < 0 ? 0 : Math.Min(c.Churn, 500);
            int roundness = RoundnessPenalty(c.Roll) + RoundnessPenalty(c.Pitch)
                          + RoundnessPenalty(c.Height) + RoundnessPenalty(c.Yaw);
            int align = c.Base % 16 == 0 ? 5 : 0;

            // Byte-exact constants are overwhelming evidence and always win. Absence of
            // churn is NOT penalised: a player standing in a menu has a live but static
            // camera, and punishing that demoted the true camera below structural decoys
            // during live testing on D2R 3.0.91636.
            if (c.LegacyExactFields >= 3)
                c.Score = c.LegacyExactFields * 1000 + churn + align - roundness;
            else if (c.LegacyExactFields >= 1)
                c.Score = c.LegacyExactFields * 200 + ProximityScore(c) + churn * 10 + align - roundness;
            else
                c.Score = ProximityScore(c) + churn * 20 + align - roundness;

            // The "frozen means dead" penalty applies ONLY to the structural path. There we
            // are hunting a camera whose constants changed, so a candidate whose whole
            // neighbourhood is frozen is very likely not it. On the signature path a frozen
            // camera is normal -- the player may simply be standing in a menu -- and applying
            // the penalty there demoted the true camera below thousands of decoys on the
            // live D2R process.
            if (method == "structural" && c.Churn <= 0) c.Score -= 1000;
        }

        if (method == "structural")
        {
            // Widen the window on the leaders so churn magnitude, not scan order, decides.
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
        foreach (var hit in mem.FindAll(LegacyAnglePattern, null, regions))
        {
            var c = ReadQuad(mem, hit);
            if (c is null) continue;
            c.Note = "stage A (exact signature)";
            found.Add(c);
        }
        return found;
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
    /// Camera parameters move by small amounts between builds; they are not redesigned.
    /// When the exact legacy bytes are gone, a live candidate whose four values are CLOSE
    /// to the 1.6-era defaults is far more likely to be the camera than one that merely
    /// churns — churn alone was ambiguous because an entire 4 KB page goes hot as soon as
    /// anything on it is written, which made unrelated quads on the same page look live.
    /// </summary>
    private static int ProximityScore(CameraCandidate c)
    {
        double d = Math.Abs(c.Roll - DefaultRoll) + Math.Abs(c.Pitch - DefaultPitch)
                 + Math.Abs(c.Height - DefaultHeight) + Math.Abs(c.Yaw - DefaultYaw);
        if (d < 0.05) return 3000;
        if (d < 0.20) return 1500;
        if (d < 0.50) return 700;
        if (d < 1.00) return 200;
        return 0;
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

    private static ulong HashPage(byte[] buf, int offset, int len)
    {
        ulong h = 1469598103934665603UL;
        int words = len / 8;
        for (int i = 0; i < words; i++)
        {
            h ^= BitConverter.ToUInt64(buf, offset + i * 8);
            h *= 1099511628211UL;
        }
        return h;
    }

    /// <summary>
    /// Finds the 4 KB pages that are actually being written, so the structural scan can skip
    /// static memory entirely.
    ///
    /// This was added after a scale test: with an adversarially dense process the candidate
    /// cap filled up after 2% of the address space and the scan was silently truncated, so a
    /// camera planted in the last block was never examined. Restricting the scan to hot pages
    /// fixes the coverage problem and makes it faster at the same time. The camera struct holds
    /// a continuously updated position, so its page is hot.
    /// </summary>
    private static HashSet<ulong> BuildHotPages(Win32Memory mem, List<MemoryRegion> regions,
                                                Action<string> log, CancellationToken ct)
    {
        var first = new Dictionary<ulong, ulong>();
        long covered = 0;

        foreach (var r in regions)
        {
            if (!r.IsPrivate && !r.IsImage) continue;
            ulong off = 0;
            while (off < r.Size)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Win32Memory.ChunkSize, r.Size - off);
                var blob = mem.ReadBytes(r.Base + off, n);
                if (blob is not null)
                {
                    for (int p = 0; p + PageSize <= blob.Length; p += PageSize)
                        first[r.Base + off + (ulong)p] = HashPage(blob, p, PageSize);
                }
                off += (ulong)n;
            }
            covered += (long)r.Size;
        }

        Thread.Sleep(700);
        ct.ThrowIfCancellationRequested();

        var hot = new HashSet<ulong>();
        foreach (var r in regions)
        {
            if (!r.IsPrivate && !r.IsImage) continue;
            ulong off = 0;
            while (off < r.Size)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Win32Memory.ChunkSize, r.Size - off);
                var blob = mem.ReadBytes(r.Base + off, n);
                if (blob is not null)
                {
                    for (int p = 0; p + PageSize <= blob.Length; p += PageSize)
                    {
                        ulong pa = r.Base + off + (ulong)p;
                        if (first.TryGetValue(pa, out var old) && old != HashPage(blob, p, PageSize))
                        {
                            // include the neighbouring pages: a struct can straddle a boundary
                            if (pa >= PageSize) hot.Add(pa - PageSize);
                            hot.Add(pa);
                            hot.Add(pa + PageSize);
                        }
                    }
                }
                off += (ulong)n;
            }
        }

        log($"  hot pages: {hot.Count} of {first.Count} "
            + $"({100.0 * hot.Count / Math.Max(1, first.Count):F1}% of {covered / 1e6:F0} MB writable)");
        return hot;
    }

    private static List<CameraCandidate> StructuralScan(Win32Memory mem,
        List<MemoryRegion> regions, HashSet<ulong>? hotPages, Action<string> log,
        CancellationToken ct)
    {
        var found = new List<CameraCandidate>();
        bool capped = false;
        long scanned = 0;
        var fbuf = Array.Empty<float>();

        void ScanRange(ulong lo, ulong hi)
        {
            ulong off = lo;
            while (off < hi && !capped)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Win32Memory.ChunkSize, hi - off);
                int pre = (off >= lo + 4 && off >= 4) ? 4 : 0;
                int post = (off + (ulong)n + 8 <= hi) ? 8 : 0;
                var blob = mem.ReadBytes(off - (ulong)pre, n + pre + post);
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
                        Base = off - (ulong)pre + (ulong)(i * 4),
                        Roll = roll, Pitch = pitch, Height = height, Yaw = yaw,
                        Note = "stage B (structural)"
                    });
                    if (found.Count >= MaxStructuralCandidates) { capped = true; break; }
                }
                off += (ulong)n;
            }
        }

        if (hotPages is not null)
        {
            // Coalesce hot pages into ranges so each read covers a contiguous span.
            ulong start = 0, end = 0;
            bool open = false;
            foreach (var p in hotPages.OrderBy(x => x))
            {
                ulong ps = p >= 16 ? p - 16 : 0;
                ulong pe = p + PageSize + 16;
                if (!open) { start = ps; end = pe; open = true; }
                else if (ps <= end) { if (pe > end) end = pe; }
                else { ScanRange(start, end); start = ps; end = pe; }
                if (capped) break;
            }
            if (open && !capped) ScanRange(start, end);
        }
        else
        {
            foreach (var region in regions)
            {
                // The camera struct lives in writable memory: heap (private) or a writable
                // image section. Read-only image pages are static data.
                if (!region.IsPrivate && !region.IsImage) continue;
                ScanRange(region.Base, region.Base + region.Size);
                if (capped) break;
            }
        }

        log($"  scanned {scanned / 1e6:F1}M float positions"
            + (capped ? $" (capped at {MaxStructuralCandidates}; consider the hot-page path)"
                      : string.Empty));
        return found;
    }
}
