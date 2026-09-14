using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WeArchive.Infrastructure.Export;

/// <summary>
/// Native interop used only to make directory metadata durable on Windows (the equivalent
/// of <c>fsync()</c> on a directory). File contents are flushed through <see cref="System.IO.FileStream.Flush(bool)"/>;
/// this is the missing barrier for the directory-rename/file-entry metadata that the export
/// commit must establish before it deletes the prior-good backup.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(IntPtr hFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Flushes the metadata of <paramref name="directoryPath"/> to media. Best-effort: a
    /// failure (missing directory, access denied, unsupported filesystem) is swallowed and
    /// the caller proceeds; on NTFS the rename/file-entry metadata is also made durable by
    /// the journal, so this is the explicit belt-and-suspenders barrier.
    /// </summary>
    internal static void FsyncDirectory(string directoryPath)
    {
        var handle = CreateFileW(
            directoryPath,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            return;
        }

        try
        {
            FlushFileBuffers(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
