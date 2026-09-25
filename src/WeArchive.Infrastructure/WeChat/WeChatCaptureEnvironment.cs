using System.Runtime.Versioning;
using WeArchive.Infrastructure.WeChat.KeyAcquisition;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// Materializes one source database as a readable SQLite image, together with the WAL
/// consistency evidence the capture adapter records. Abstracts
/// <see cref="SqlCipherDatabaseCache"/> so the adapter can be exercised against a fixture source
/// without a live client or a real database key.
/// </summary>
internal interface IWeChatSourceMaterializer : IDisposable
{
    DecryptionOutcome GetPlaintext(string databasePath);
}

/// <summary>
/// The live-source facts and side effects the WeChat capture adapter depends on: which local
/// accounts and databases exist, whether the client is running, the observed client version, and
/// how a database is materialized as readable content.
/// <para>
/// Production delegates to <see cref="WeChatDataLocator"/>, <see cref="WeChatClient"/> and
/// <see cref="SqlCipherDatabaseCache"/>. Tests supply a fixture implementation, which is what
/// makes the shipped fingerprint/prior-map/reuse decision coverable without a live WeChat client
/// (docs/PRD.md NFR-06, Issue #25).
/// </para>
/// </summary>
internal interface IWeChatCaptureEnvironment
{
    /// <summary>Every local account this machine exposes, across all discovered installations.</summary>
    IReadOnlyList<WeChatAccountLocation> DiscoverAccounts();

    /// <summary>All source database files belonging to one account.</summary>
    IReadOnlyList<string> EnumerateDatabases(WeChatAccountLocation account);

    /// <summary>The detected client version, or null when it cannot be determined.</summary>
    string? DetectClientVersion();

    /// <summary>
    /// Whether the client is running. The database key can only be recovered from a running,
    /// signed-in client, so a capture that must materialize new evidence fails without it.
    /// </summary>
    bool IsClientRunning();

    /// <summary>Creates the materializer used for the databases of one capture run.</summary>
    IWeChatSourceMaterializer CreateMaterializer(WeChatKeySet keys);
}

/// <summary>The production environment: real discovery, real client probing, real SQLCipher.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WeChatCaptureEnvironment : IWeChatCaptureEnvironment
{
    public IReadOnlyList<WeChatAccountLocation> DiscoverAccounts() =>
        [.. WeChatDataLocator.Discover().SelectMany(installation => installation.Accounts)];

    public IReadOnlyList<string> EnumerateDatabases(WeChatAccountLocation account) =>
        WeChatDataLocator.EnumerateDatabases(account);

    public string? DetectClientVersion() => WeChatClient.DetectInstallation().Version;

    public bool IsClientRunning() => WeChatClient.IsRunning();

    public IWeChatSourceMaterializer CreateMaterializer(WeChatKeySet keys) =>
        new SqlCipherSourceMaterializer(new SqlCipherDatabaseCache(keys));

    [SupportedOSPlatform("windows")]
    private sealed class SqlCipherSourceMaterializer(SqlCipherDatabaseCache cache) : IWeChatSourceMaterializer
    {
        public DecryptionOutcome GetPlaintext(string databasePath) => cache.GetPlaintext(databasePath);

        public void Dispose() => cache.Dispose();
    }
}