using System.ComponentModel;
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
    /// Flushes the metadata of <paramref name="directoryPath"/> to media (the Windows equivalent
    /// of <c>fsync()</c> on a directory). A native failure — the directory is missing, access is
    /// denied, or the filesystem cannot flush — is surfaced as a <see cref="Win32Exception"/>
    /// carrying <see cref="Marshal.GetLastWin32Error"/>, so the export commit's durability barrier
    /// can fail the commit and restore the prior-good backup instead of deleting it after a barrier
    /// that persisted nothing. Treating the flush as best-effort would contradict the
    /// docs/EXPORT_PRD.md §§3.2/15 contract that a failed barrier restores the last good dataset.
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
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Failed to open the directory for a durability flush: {directoryPath}");
        }

        try
        {
            var flushed = FlushFileBuffers(handle);
            if (!flushed)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Failed to flush directory metadata to media: {directoryPath}");
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
