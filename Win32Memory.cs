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

    public int ProcessId { get; private set; }
    public bool IsOpen => _handle != IntPtr.Zero;
    public string ProcessName { get; private set; } = string.Empty;

    // ---------------------------------------------------------------- lifecycle

    public bool OpenByName(string processName, bool writeAccess = true)
    {
        string bare = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;
        var procs = System.Diagnostics.Process.GetProcessesByName(bare);
        Trace?.Invoke($"GetProcessesByName('{bare}') -> {procs.Length} process(es)");
        try
        {
            foreach (var p in procs)
            {
                if (Open(p.Id, writeAccess))
                {
                    ProcessName = p.ProcessName;
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
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
    /// Linear byte-pattern scan. <paramref name="mask"/> entries of 0x00 are wildcards;
    /// a null mask means every byte must match.
    /// </summary>
    public IEnumerable<ulong> FindPattern(byte[] pattern, byte[]? mask = null,
                                          List<MemoryRegion>? regions = null,
                                          Action<int>? onRegion = null)
    {
        if (!IsOpen || pattern.Length == 0) yield break;
        regions ??= EnumerateRegions();
        int plen = pattern.Length;
        byte first = pattern[0];
        bool firstIsWild = mask != null && mask[0] == 0;

        int regionIndex = 0;
        foreach (var region in regions)
        {
            onRegion?.Invoke(regionIndex++);
            ulong off = 0;
            while (off < region.Size)
            {
                int n = (int)Math.Min((ulong)ChunkSize, region.Size - off);
                var chunk = ReadBytes(region.Base + off, n);
                if (chunk is null) { off += (ulong)n; continue; }

                int limit = chunk.Length - plen;
                for (int i = 0; i <= limit; i++)
                {
                    if (!firstIsWild && chunk[i] != first) continue;
                    bool ok = true;
                    for (int j = 0; j < plen; j++)
                    {
                        if (mask != null && mask[j] == 0) continue;
                        if (chunk[i + j] != pattern[j]) { ok = false; break; }
                    }
                    if (ok) yield return region.Base + off + (ulong)i;
                }
                off += (ulong)n;
            }
        }
    }

    // ---------------------------------------------------------------- P/Invoke

    private static class Native
    {
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
