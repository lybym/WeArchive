using System.Runtime.Versioning;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>One local WeChat account data directory (<c>&lt;root&gt;\&lt;account&gt;_&lt;suffix&gt;</c>).</summary>
internal sealed record WeChatAccountLocation(
    string SourceProfileId,
    string DataDirectory,
    string DatabaseDirectory,
    DateTimeOffset? LastActiveAt);

/// <summary>A discovered local WeChat data root together with the accounts it contains.</summary>
internal sealed record WeChatInstallation(
    string DataRootPath,
    IReadOnlyList<WeChatAccountLocation> Accounts,
    string? ClientVersion);

/// <summary>
/// Locates local WeChat 4.x data directories.
/// <para>
/// Only the current user's own profile is considered, and nothing is ever written to the
/// discovered directories. See docs/PRD.md NFR-02.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WeChatDataLocator
{
    private static readonly string[] KnownNonAccountDirectories = ["all_users", "Backup", "temp"];

    public static IReadOnlyList<WeChatInstallation> Discover()
    {
        var results = new List<WeChatInstallation>();
        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            var accounts = DiscoverAccounts(root);
            if (accounts.Count == 0)
            {
                continue;
            }

            results.Add(new WeChatInstallation(root, accounts, WeChatClient.DetectInstallation().Version));
        }

        return results;
    }

    public static IEnumerable<string> CandidateRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, "xwechat_files");

        // Documents may be redirected (for example onto OneDrive).
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(documents))
        {
            yield return Path.Combine(documents, "xwechat_files");
            yield return Path.Combine(Directory.GetParent(documents)?.FullName ?? documents, "Documents", "xwechat_files");
        }
    }

    private static List<WeChatAccountLocation> DiscoverAccounts(string root)
    {
        var logins = DiscoverLoginNames(root);
        var accounts = new List<WeChatAccountLocation>();

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (KnownNonAccountDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var databaseDirectory = Path.Combine(directory, "db_storage");
            if (!Directory.Exists(databaseDirectory))
            {
                continue;
            }

            accounts.Add(new WeChatAccountLocation(
                ResolveProfileId(name, logins),
                directory,
                databaseDirectory,
                Directory.GetLastWriteTimeUtc(databaseDirectory)));
        }

        return accounts
            .OrderByDescending(a => a.LastActiveAt)
            .ToList();
    }

    private static HashSet<string> DiscoverLoginNames(string root)
    {
        var logins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loginRoot = Path.Combine(root, "all_users", "login");
        if (!Directory.Exists(loginRoot))
        {
            return logins;
        }

        foreach (var directory in Directory.EnumerateDirectories(loginRoot))
        {
            logins.Add(Path.GetFileName(directory));
        }

        return logins;
    }

    /// <summary>
    /// The account directory is <c>&lt;login-name&gt;_&lt;suffix&gt;</c>. The login name is the
    /// stable source profile id; it is cross-checked against <c>all_users\login</c> so a
    /// directory whose name merely contains an underscore is not truncated incorrectly.
    /// </summary>
    private static string ResolveProfileId(string directoryName, HashSet<string> logins)
    {
        if (logins.Contains(directoryName))
        {
            return directoryName;
        }

        foreach (var login in logins)
        {
            if (directoryName.StartsWith(login + "_", StringComparison.OrdinalIgnoreCase))
            {
                return login;
            }
        }

        var separator = directoryName.LastIndexOf('_');
        return separator > 0 ? directoryName[..separator] : directoryName;
    }

    /// <summary>All encrypted database files belonging to an account.</summary>
    public static IReadOnlyList<string> EnumerateDatabases(WeChatAccountLocation account) =>
        Directory.Exists(account.DatabaseDirectory)
            ? [.. Directory.EnumerateFiles(account.DatabaseDirectory, "*.db", SearchOption.AllDirectories)]
            : [];

    public static string SessionDatabase(WeChatAccountLocation account) =>
        Path.Combine(account.DatabaseDirectory, "session", "session.db");

    public static string ContactDatabase(WeChatAccountLocation account) =>
        Path.Combine(account.DatabaseDirectory, "contact", "contact.db");

    public static IReadOnlyList<string> MessageDatabases(WeChatAccountLocation account)
    {
        var directory = Path.Combine(account.DatabaseDirectory, "message");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(directory, "message_*.db")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
        ];
    }
}
