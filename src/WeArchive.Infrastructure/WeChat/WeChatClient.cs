using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// Facts about the installed Windows WeChat client. Future client generations may need a
/// sibling type; the rest of the adapter only depends on this surface.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WeChatClient
{
    /// <summary>WeChat 4.x process name.</summary>
    public const string WeChatProcessName = "Weixin.exe";

    /// <summary>WeChat 3.x process name, kept so the diagnostic text is accurate.</summary>
    public const string LegacyWeChatProcessName = "WeChat.exe";

    public const string ProductName = "WeChat for Windows";

    private static readonly string[] InstallRootCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "Weixin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "Weixin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "WeChat"),
    ];

    /// <summary>
    /// Determines the installed client version.
    /// <para>
    /// The running process's own module version is authoritative. When WeChat is not running,
    /// the version is taken from the versioned program directory (for example
    /// <c>Program Files\Tencent\Weixin\4.1.13.12</c>), which is where the client keeps its
    /// binaries even though the launcher executable sits one level above it.
    /// </para>
    /// </summary>
    public static (string? Version, string? ExecutablePath) DetectInstallation()
    {
        var running = DetectFromRunningProcess();
        if (running.Version is not null)
        {
            return running;
        }

        string? bestVersion = null;
        string? bestExecutable = null;
        Version? bestParsed = null;

        foreach (var root in InstallRootCandidates)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var launcher = FirstExisting(
                Path.Combine(root, WeChatProcessName),
                Path.Combine(root, LegacyWeChatProcessName));

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (!Version.TryParse(name, out var parsed))
                {
                    continue;
                }

                if (bestParsed is not null && parsed <= bestParsed)
                {
                    continue;
                }

                bestParsed = parsed;
                bestVersion = name;
                bestExecutable = launcher ?? FirstExisting(
                    Path.Combine(directory, WeChatProcessName),
                    Path.Combine(directory, LegacyWeChatProcessName));
            }

            if (bestVersion is null && launcher is not null)
            {
                var fileVersion = ReadFileVersion(launcher);
                if (fileVersion is not null)
                {
                    bestVersion = fileVersion;
                    bestExecutable = launcher;
                }
            }
        }

        return (bestVersion, bestExecutable);
    }

    private static (string? Version, string? ExecutablePath) DetectFromRunningProcess()
    {
        foreach (var name in (string[])[WeChatProcessName, LegacyWeChatProcessName])
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        var module = process.MainModule;
                        if (module is null)
                        {
                            continue;
                        }

                        var version = Normalize(module.FileVersionInfo?.FileVersion);
                        if (version is not null)
                        {
                            return (version, module.FileName);
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // A process we may not inspect is simply skipped.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return (null, null);
    }

    private static string? ReadFileVersion(string path)
    {
        try
        {
            return Normalize(FileVersionInfo.GetVersionInfo(path).FileVersion);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A file version can carry a trailing build suffix; only dotted numbers are kept.</summary>
    private static string? Normalize(string? fileVersion)
    {
        if (string.IsNullOrWhiteSpace(fileVersion))
        {
            return null;
        }

        var value = fileVersion.Trim();
        return Version.TryParse(value, out _) ? value : null;
    }

    private static string? FirstExisting(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool IsRunning() =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WeChatProcessName)).Length > 0;

    public static string FormatVersion(string? version) =>
        version is null ? "unknown" : version;

    public static string DescribeInstallation(string? version) =>
        version is null
            ? $"{ProductName} (not detected)"
            : string.Create(CultureInfo.InvariantCulture, $"{ProductName} {version}");
}
