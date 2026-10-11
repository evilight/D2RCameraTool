using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace D2RCameraTool;

internal readonly record struct MemoryRegion(ulong Base, ulong Size, uint Protect, uint Type)
{
    public bool IsPrivate => Type == 0x20000;
    public bool IsImage => Type == 0x1000000;
}

/// <summary>
/// Minimal Win32 process-memory layer, replacing the third-party Memory.dll
/// dependency (a prebuilt binary that is both unpatchable and a known source of
/// antivirus false positives).
///
/// The read-only handle path is used for scanning so that locating the camera
/// never requires write access to the game.
/// </summary>
internal sealed class Win32Memory : IDisposable
{
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_VM_OPERATION = 0x0008;

    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_GUARD = 0x100;
    private const uint READABLE_MASK = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    /// <summary>Chunk size for large reads; keeps peak allocation bounded.</summary>
    public const int ChunkSize = 1 << 25; // 32 MB

    private const ulong MaxUserAddress = 0x7FFFFFFF0000UL;

    private IntPtr _handle = IntPtr.Zero;

    /// <summary>Optional diagnostic sink so the CLI can explain an OpenProcess failure.</summary>
    public static Action<string>? Trace;

    /// <summary>Win32 error from the most recent OpenProcess call.</summary>
    public int LastOpenError { get; private set; }

    /// <summary>
    /// Human-readable reason for the last failed OpenByName. Without this the GUI sat on its
    /// initial status text forever and looked hung, giving no clue whether the game was
    /// missing, the name was wrong, or access was refused.
    /// </summary>
    public string LastLookupNote { get; private set; } = string.Empty;

    /// <summary>Number of processes the most recent enumeration returned.</summary>
    public int LastEnumerateCount { get; private set; }

    public int ProcessId { get; private set; }
    public bool IsOpen => _handle != IntPtr.Zero;
    public string ProcessName { get; private set; } = string.Empty;

    // ---------------------------------------------------------------- lifecycle

    public bool OpenByName(string processName, bool writeAccess = true)
    {
        string bare = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        // Enumerate with the raw Toolhelp32 API rather than System.Diagnostics.Process.
        // Observed in a sandboxed host: Process.GetProcessesByName returned 0 processes for
        // everything outside the host's own job object, while an identical native
        // Toolhelp32 walk from Python saw the whole session -- the game could not be found
        // at all. The native walk is also the more predictable API here.
        // Toolhelp32 reports the image name WITH its extension ("D2R.exe"), while the GUI
        // default and the managed API use the bare name. Compare against both, otherwise
        // the native walk silently never matches and every call falls through to the slower
        // managed path.
        var all = EnumerateProcesses();
        LastEnumerateCount = all.Count;
        var pids = all
            .Where(p => string.Equals(p.name, bare, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(p.name, bare + ".exe", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.pid)
            .ToList();

        if (all.Count == 0)
            LastLookupNote = "no processes enumerated (host restricts visibility)";
        else if (pids.Count == 0)
            LastLookupNote = $"'{bare}' not among {all.Count} processes";
        else
            LastLookupNote = $"'{bare}' matches pid {string.Join(",", pids)}";

        Trace?.Invoke($"Toolhelp32: {LastLookupNote}");

        foreach (var pid in pids)
        {
            if (Open(pid, writeAccess))
            {
                ProcessName = bare;
                LastLookupNote = $"attached pid {pid}";
                return true;
            }
        }

        // Fall back to the managed API in case the native walk was itself restricted.
        if (pids.Count == 0)
        {
            var procs = System.Diagnostics.Process.GetProcessesByName(bare);
            Trace?.Invoke($"fallback GetProcessesByName('{bare}') -> {procs.Length}");
            if (procs.Length > 0)
                LastLookupNote = $"Toolhelp32 missed it, managed API matched {procs.Length}";
            try
            {
                foreach (var p in procs)
                {
                    if (Open(p.Id, writeAccess))
                    {
                        ProcessName = p.ProcessName;
                        LastLookupNote = $"attached pid {p.Id} (managed API)";
                        return true;
                    }
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        if (pids.Count > 0 || LastEnumerateCount > 0)
            LastLookupNote += LastOpenError == 5
                ? " - OpenProcess denied (err 5), try running as administrator"
                : $" - OpenProcess failed (err {LastOpenError})";
        return false;
    }

    /// <summary>Full process list via CreateToolhelp32Snapshot.</summary>
    public static List<(int pid, string name)> EnumerateProcesses()
    {
        var list = new List<(int, string)>();
        IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return list;
        try
        {
            var e = new Native.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>() };
            if (!Native.Process32FirstW(snap, ref e)) return list;
            do
            {
                list.Add(((int)e.th32ProcessID, e.szExeFile ?? string.Empty));
                e.dwSize = (uint)Marshal.SizeOf<Native.PROCESSENTRY32W>();
            }
            while (Native.Process32NextW(snap, ref e));
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return list;
    }

    public bool Open(int pid, bool writeAccess = true)
    {
        Close();
        uint access = PROCESS_QUERY_INFORMATION | PROCESS_VM_READ;
        if (writeAccess) access |= PROCESS_VM_WRITE | PROCESS_VM_OPERATION;

        // Try plainly first. Asking for SeDebugPrivilege up front made OpenProcess fail
        // with ACCESS_DENIED here while an identical ctypes call from Python succeeded,
        // so the privilege request is only issued when a plain attempt is refused.
        _handle = Native.OpenProcess(access, false, pid);
        LastOpenError = Marshal.GetLastWin32Error();
        bool priv = false;

        if (_handle == IntPtr.Zero && LastOpenError == 5)
        {
            priv = TryEnableDebugPrivilege();
            _handle = Native.OpenProcess(access, false, pid);
            LastOpenError = Marshal.GetLastWin32Error();
        }

        Trace?.Invoke($"OpenProcess(pid={pid}, access=0x{access:X}) -> "
                      + $"{(_handle == IntPtr.Zero ? "NULL" : "handle")} err={LastOpenError} "
                      + $"debugPrivilegeUsed={priv}");
        if (_handle == IntPtr.Zero) return false;
        ProcessId = pid;
        return true;
    }

    /// <summary>
    /// Best-effort SeDebugPrivilege. Succeeds silently when the process is not
    /// elevated; callers should surface a "run as administrator" hint if OpenProcess
    /// still fails with ERROR_ACCESS_DENIED (5).
    /// </summary>
    public static bool TryEnableDebugPrivilege()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(),
                                     Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY,
                                     out IntPtr token))
        {
            return false;
        }
        try
        {
            if (!Native.LookupPrivilegeValue(null, "SeDebugPrivilege", out Native.LUID luid))
                return false;

            var tp = new Native.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = Native.SE_PRIVILEGE_ENABLED,
            };
            return Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    public static int LastError => Marshal.GetLastWin32Error();

    public void Close()
    {
        if (_handle != IntPtr.Zero)
        {
            Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        ProcessId = 0;
    }

    public void Dispose() => Close();

    // ---------------------------------------------------------------- raw access

    public byte[]? ReadBytes(ulong address, int size)
    {
        if (!IsOpen || size <= 0) return null;
        var buf = new byte[size];
        if (Native.ReadProcessMemory(_handle, (IntPtr)(long)address, buf, (IntPtr)size, out IntPtr read)
            && read.ToInt64() == size)
        {
            return buf;
        }
        // A short read can still be useful for block reads near a region edge.
        long got = read.ToInt64();
        if (got > 0)
        {
            var part = new byte[got];
            Array.Copy(buf, part, got);
            return part;
        }
        return null;
    }

    public bool WriteBytes(ulong address, byte[] bytes)
    {
        if (!IsOpen || bytes.Length == 0) return false;
        return Native.WriteProcessMemory(_handle, (IntPtr)(long)address, bytes, (IntPtr)bytes.Length,
                                         out IntPtr written)
               && written.ToInt64() == bytes.Length;
    }

    public float? ReadFloat(ulong address)
    {
        var b = ReadBytes(address, 4);
        return b is { Length: 4 } ? BitConverter.ToSingle(b, 0) : null;
    }

    /// <summary>
    /// Writes the four bytes directly. The original tool round-tripped through
    /// float.ToString(), which is culture-dependent: under a locale whose decimal
    /// separator is a comma the value would be misparsed.
    /// </summary>
    public bool WriteFloat(ulong address, float value)
        => WriteBytes(address, BitConverter.GetBytes(value));

    // ---------------------------------------------------------------- regions

    public List<MemoryRegion> EnumerateRegions(bool readableOnly = true)
    {
        var list = new List<MemoryRegion>();
        if (!IsOpen) return list;

        ulong addr = 0;
        int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
        while (addr < MaxUserAddress)
        {
            if (Native.VirtualQueryEx(_handle, (IntPtr)(long)addr,
                                      out Native.MEMORY_BASIC_INFORMATION mbi,
                                      (IntPtr)mbiSize) != (IntPtr)mbiSize)
            {
                break;
            }
            ulong b = (ulong)mbi.BaseAddress.ToInt64();
            ulong size = (ulong)mbi.RegionSize.ToInt64();
            if (size == 0) break;

            bool usable = mbi.State == MEM_COMMIT
                          && (mbi.Protect & READABLE_MASK) != 0
                          && (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) == 0;
            if (!readableOnly || usable)
                list.Add(new MemoryRegion(b, size, mbi.Protect, mbi.Type));

            ulong next = b + size;
            if (next <= addr) break; // guard against a non-advancing walk
            addr = next;
        }
        return list;
    }

    // ---------------------------------------------------------------- scanning

    /// <summary>
    /// Finds every occurrence of <paramref name="pattern"/> (mask bytes of 0x00 are
    /// wildcards) across the given regions.
    ///
    /// Two things make this fast enough for a multi-gigabyte process:
    ///
    ///  * the search is anchored on the pattern's LONGEST LITERAL RUN and driven by the
    ///    vectorised span search, rather than stepping one byte at a time;
    ///  * regions are scanned in parallel.
    ///
    /// The original hand-written byte loop needed about 78 s to walk 8.75 GB of live game
    /// memory and return two hits.
    /// </summary>
    public List<ulong> FindAll(byte[] pattern, byte[]? mask = null,
                               List<MemoryRegion>? regions = null,
                               CancellationToken ct = default)
    {
        var hits = new List<ulong>();
        if (!IsOpen || pattern.Length == 0) return hits;
        regions ??= EnumerateRegions();
        if (regions.Count == 0) return hits;

        var (anchor, runStart) = LongestLiteralRun(pattern, mask);

        // Leave a couple of cores for the game and the UI thread.
        int dop = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        object gate = new object();

        Parallel.ForEach(
            Partitioner.Create(0, regions.Count),
            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
            range =>
            {
                var local = new List<ulong>();
                for (int ri = range.Item1; ri < range.Item2; ri++)
                {
                    ct.ThrowIfCancellationRequested();
                    var region = regions[ri];
                    ulong off = 0;
                    while (off < region.Size)
                    {
                        int n = (int)Math.Min((ulong)ChunkSize, region.Size - off);
                        var chunk = ReadBytes(region.Base + off, n);
                        if (chunk is not null)
                            FindInChunk(chunk, pattern, mask, anchor, runStart,
                                        region.Base + off, local);
                        off += (ulong)n;
                    }
                }
                if (local.Count > 0) lock (gate) hits.AddRange(local);
            });

        return hits;
    }

    /// <summary>
    /// Returns the longest run of non-wildcard bytes and its offset in the pattern. For a
    /// mask-free pattern this is the whole pattern, which lets the vectorised substring
    /// search do all the work.
    /// </summary>
    private static (byte[] anchor, int runStart) LongestLiteralRun(byte[] pattern, byte[]? mask)
    {
        int bestStart = 0, bestLen = 0, curStart = 0, curLen = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            bool literal = mask is null || mask[i] != 0;
            if (literal)
            {
                if (curLen == 0) curStart = i;
                curLen++;
                if (curLen > bestLen) { bestLen = curLen; bestStart = curStart; }
            }
            else
            {
                curLen = 0;
            }
        }

        if (bestLen == 0) { bestStart = 0; bestLen = 1; }   // all wildcards: degenerate

        var anchor = new byte[bestLen];
        Array.Copy(pattern, bestStart, anchor, 0, bestLen);
        return (anchor, bestStart);
    }

    /// <summary>Collects matches for one chunk into <paramref name="hits"/>.</summary>
    private static void FindInChunk(byte[] chunk, byte[] pattern, byte[]? mask,
                                    byte[] anchor, int runStart, ulong chunkBase,
                                    List<ulong> hits)
    {
        int plen = pattern.Length;
        if (chunk.Length < plen) return;

        ReadOnlySpan<byte> span = chunk;
        int lastStart = chunk.Length - plen;          // last valid pattern offset
        int maxAnchor = lastStart + runStart;          // last valid anchor offset
        int from = 0;

        while (from <= maxAnchor)
        {
            int rel = span.Slice(from, maxAnchor - from + 1).IndexOf(anchor);
            if (rel < 0) return;
            int anchorPos = from + rel;
            int patPos = anchorPos - runStart;

            if (patPos >= 0)
            {
                bool ok = true;
                for (int j = 0; j < plen; j++)
                {
                    if (mask is not null && mask[j] == 0) continue;
                    if (span[patPos + j] != pattern[j]) { ok = false; break; }
                }
                if (ok) hits.Add(chunkBase + (ulong)patPos);
            }

            from = anchorPos + 1;
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    private static class Native
    {
        internal const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        internal const uint TOKEN_QUERY = 0x0008;
        internal const uint SE_PRIVILEGE_ENABLED = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public uint __alignment1;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint __alignment2;
        }

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess,
                                                     out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            [Out] byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress,
            byte[] lpBuffer, IntPtr dwSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);
    }
}
