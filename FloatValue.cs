using System;

namespace D2RCameraTool
{
    /// <summary>
    /// One camera float: its resolved address, its original bytes (used both as the
    /// reset value and as a fingerprint) and read/write access.
    ///
    /// Backed by <see cref="Win32Memory"/> instead of Memory.dll. Writes go through
    /// BitConverter rather than a float-to-string round trip, which was
    /// culture-dependent in the original implementation.
    /// </summary>
    internal class FloatValue
    {
        public long BaseAddress;
        public string BaseString = string.Empty;
        public byte[] DefaultBytes;
        public float LastValue;

        private static Win32Memory Mem => Program.MemLib;

        public FloatValue(byte[]? defaultBytes = null)
        {
            if (defaultBytes == null || defaultBytes.Length <= 0)
            {
                defaultBytes = new byte[] { 0x00, 0x00, 0x00, 0x00 };
            }
            DefaultBytes = defaultBytes;
            LastValue = Read();
        }

        public void Reset() => Write(DefaultBytes);
        public void Write() => Write(DefaultBytes);

        public string ReadHex() => Program.FloatToHex(Read());

        public void UpdateBase(long baseAddr)
        {
            BaseAddress = baseAddr;
            BaseString = "0x" + baseAddr.ToString("X");
        }

        public float Read()
        {
            if (BaseAddress <= 0 || Mem is null || !Mem.IsOpen)
                return 0;

            var v = Mem.ReadFloat((ulong)BaseAddress);
            if (v is null)
                return LastValue;

            LastValue = v.Value;
            return LastValue;
        }

        public void Write(byte[] bytes)
        {
            if (BaseAddress <= 0 || bytes == null || bytes.Length <= 0)
                return;
            if (Mem is null || !Mem.IsOpen)
                return;

            Mem.WriteBytes((ulong)BaseAddress, bytes);
        }

        public bool Write(float newValue)
        {
            if (BaseAddress <= 0 || Mem is null || !Mem.IsOpen)
                return false;

            if (Mem.WriteFloat((ulong)BaseAddress, newValue))
            {
                LastValue = newValue;
                return true;
            }

            UpdateBase(0);
            return false;
        }
    }
}
