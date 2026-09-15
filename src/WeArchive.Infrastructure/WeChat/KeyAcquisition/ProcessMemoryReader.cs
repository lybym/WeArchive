using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WeArchive.Infrastructure.WeChat.KeyAcquisition;

/// <summary>
/// Minimal read-only window into another process's address space.
/// <para>
/// Used only to recover the local archive key that WeChat itself holds in memory while
/// it is running. This code never writes to the target process, never injects code and
/// never calls into it; it only issues <c>VirtualQueryEx</c>/<c>ReadProcessMemory</c>.
/// See docs/adr/0005-wechat-local-key-acquisition.md.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessMemoryReader
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageGuard = 0x100;
    private const uint PageNoAccess = 0x01;
    private const uint Th32csSnapProcess = 0x00000002;
    private const int MaxRegionBytes = 64 * 1024 * 1024;

    /// <summary>Protection flags under which a committed region is readable.</summary>
    private static readonly uint[] ReadableProtections = [0x02, 0x04, 0x08, 0x20, 0x40, 0x80];

    public static IReadOnlyList<int> FindProcessIds(string executableName)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return [];
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return [];
            }

            var result = new List<int>();
            do
            {
                if (string.Equals(entry.ExecutableFile, executableName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add((int)entry.ProcessId);
                }

                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32Next(snapshot, ref entry));

            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// Reads every committed, readable region of the process and hands it to
    /// <paramref name="visitor"/>. Regions larger than 64 MiB are yielded in slices so
    /// a single allocation never dominates.
    /// </summary>
    public static void EnumerateReadableMemory(int processId, Action<byte[], long> visitor)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var address = 0L;
            var info = default(MemoryBasicInformation);
            var infoSize = Marshal.SizeOf<MemoryBasicInformation>();

            while (VirtualQueryEx(handle, new IntPtr(address), out info, (IntPtr)infoSize) == (IntPtr)infoSize)
            {
                var regionBase = info.BaseAddress.ToInt64();
                var regionSize = (long)info.RegionSize;

                if (IsReadable(info))
                {
                    var remaining = regionSize;
                    var cursor = regionBase;
                    while (remaining > 0)
                    {
                        var chunk = (int)Math.Min(remaining, MaxRegionBytes);
                        var buffer = new byte[chunk];
                        if (ReadProcessMemory(handle, new IntPtr(cursor), buffer, (IntPtr)chunk, out var read)
                            && read.ToInt64() > 0)
                        {
                            visitor(buffer, cursor);
                        }

                        cursor += chunk;
                        remaining -= chunk;
                    }
                }

                if (regionBase + regionSize <= regionBase)
                {
                    break;
                }

                address = regionBase + regionSize;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool IsReadable(MemoryBasicInformation info)
    {
        if (info.State != MemCommit
            || (info.Protect & PageGuard) != 0
            || (info.Protect & PageNoAccess) != 0)
        {
            return false;
        }

        var protection = info.Protect & 0xFF;
        return Array.IndexOf(ReadableProtections, protection) >= 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        IntPtr size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualQueryEx(
        IntPtr process,
        IntPtr address,
        out MemoryBasicInformation buffer,
        IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    internal static string DescribeLastError() =>
        new Win32Exception(Marshal.GetLastWin32Error()).Message;
}
