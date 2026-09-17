using System.Runtime.Versioning;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.WeChat.KeyAcquisition;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// WeChat 4.x capture adapter: produces a consistent source snapshot by materializing each
/// encrypted SQLCipher database as a decrypted, readable SQLite image and writing it into the
/// Raw Vault as an opaque artifact.
/// <para>
/// The consistent-snapshot strategy reuses <see cref="SqlCipherDatabaseCache"/>, which opens
/// source files with shared read access, replays only committed, HMAC-verified WAL frames and
/// never modifies the source. The resulting plaintext images are source-faithful: they
/// preserve every table, column and row, including fields the current parser cannot interpret
/// (Issue #22 unknown-field preservation).
/// </para>
/// <para>
/// The upstream WeChat key is held only in memory (<see cref="WeChatKeySet"/>) and is never
/// written to the Raw Vault, the canonical SQLite, logs or CLI output. Once the capture is
/// done the scratch cache is disposed, so the key and decrypted scratch material do not
/// survive (Issue #22 key non-persistence).
/// </para>
/// <para>
/// This class stays inside the WeChat infrastructure boundary: no WeChat table names, column
/// names or message type codes leak into Core, CLI, query or export layers.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WeChatCaptureAdapter : ISourceCaptureAdapter, IDisposable
{
    public const string Family = "wechat-windows";
    public const string Version = "0.1.0";

    private const string SourceDatabaseRole = "source-database";

    private readonly IWeChatDatabaseKeyAcquirer _keyAcquirer;
    private bool _disposed;

    public WeChatCaptureAdapter()
        : this(new WcdbCipherConfigKeyAcquirer())
    {
    }

    internal WeChatCaptureAdapter(IWeChatDatabaseKeyAcquirer keyAcquirer)
    {
        _keyAcquirer = keyAcquirer ?? throw new ArgumentNullException(nameof(keyAcquirer));
    }

    public string CaptureAdapterFamily => Family;

    public string CaptureAdapterVersion => Version;

    public async Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new CaptureProgress
        {
            Stage = CaptureStages.Acquiring,
            Detail = sourceProfileId,
        });

        // Discover the account's data directory and all its encrypted databases.
        var installations = WeChatDataLocator.Discover();
        var account = installations
            .SelectMany(i => i.Accounts)
            .FirstOrDefault(a => string.Equals(a.SourceProfileId, sourceProfileId, StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            return new SourceCaptureResult
            {
                Artifacts = [],
                Completeness = RawGenerationCompleteness.Incomplete,
                Diagnostics = [RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.SourceUnavailable,
                    $"No local WeChat account '{sourceProfileId}' was found for capture.")],
            };
        }

        var databases = WeChatDataLocator.EnumerateDatabases(account);
        if (databases.Count == 0)
        {
            return new SourceCaptureResult
            {
                Artifacts = [],
                Completeness = RawGenerationCompleteness.Incomplete,
                Diagnostics = [RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.PartitionMissing,
                    $"Account '{sourceProfileId}' has no readable database files to capture.")],
            };
        }

        // Acquire the transient key set. The key is verified against real database pages and
        // is held only in memory for the duration of this capture.
        if (!WeChatClient.IsRunning())
        {
            return new SourceCaptureResult
            {
                Artifacts = [],
                Completeness = RawGenerationCompleteness.Incomplete,
                Diagnostics = [RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.KeyAcquisitionFailed,
                    "WeChat is not running. The local database key can only be recovered while " +
                    "the client is running and signed in.")],
            };
        }

        var keyResult = _keyAcquirer.Acquire(databases);
        if (!keyResult.Succeeded || keyResult.KeySet is null)
        {
            return new SourceCaptureResult
            {
                Artifacts = [],
                Completeness = RawGenerationCompleteness.Incomplete,
                Diagnostics = [RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.KeyAcquisitionFailed,
                    keyResult.Message)],
            };
        }

        var (clientVersion, _) = WeChatClient.DetectInstallation();
        var diagnostics = new List<RawManifestDiagnostic>();
        var artifacts = new List<RawArtifactDescriptor>();
        var completeness = RawGenerationCompleteness.Complete;

        using var cache = new SqlCipherDatabaseCache(keyResult.KeySet);

        progress?.Report(new CaptureProgress
        {
            Stage = CaptureStages.Snapshotting,
            Total = databases.Count,
            Detail = sourceProfileId,
        });

        for (var i = 0; i < databases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var databasePath = databases[i];

            DecryptionOutcome outcome;
            try
            {
                outcome = cache.GetPlaintext(databasePath);
            }
            catch (WeChatKeyUnavailableException ex)
            {
                // A specific database whose key could not be resolved is partial coverage,
                // not a fatal abort: the rest of the account may still be captured.
                diagnostics.Add(RawManifestDiagnostic.Partial(
                    DiagnosticCodes.PartitionUnreadable,
                    ex.Message));
                completeness = RawGenerationCompleteness.Partial;
                progress?.Report(new CaptureProgress
                {
                    Stage = CaptureStages.Snapshotting,
                    Processed = i + 1,
                    Total = databases.Count,
                });
                continue;
            }

            // Report WAL consistency information: rejected frames mean the snapshot is
            // consistent up to the last committed transaction, but some torn frames were
            // skipped. This is Partial, not Fatal: the image is still valid.
            if (outcome.WalFramesRejected > 0)
            {
                diagnostics.Add(RawManifestDiagnostic.Partial(
                    DiagnosticCodes.WalFramesRejected,
                    $"{Path.GetFileName(databasePath)}: rejected {outcome.WalFramesRejected} " +
                    "torn/stale WAL frame(s); the snapshot is consistent up to the last committed " +
                    "transaction."));
                if (completeness == RawGenerationCompleteness.Complete)
                {
                    completeness = RawGenerationCompleteness.Partial;
                }
            }

            var artifactName = Path.GetFileName(databasePath);
            var relativePath = Path.GetRelativePath(account.DatabaseDirectory, databasePath);

            await using var stream = new FileStream(
                outcome.PlaintextPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete);

            var descriptor = await session
                .WriteArtifactAsync(
                    SourceDatabaseRole,
                    artifactName,
                    stream,
                    sourceFormat: "sqlite",
                    isDecrypted: !outcome.WasPlaintext,
                    metadata: new Dictionary<string, string>
                    {
                        ["page_count"] = outcome.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["wal_frames_applied"] = outcome.WalFramesApplied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["wal_frames_rejected"] = outcome.WalFramesRejected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["was_plaintext"] = outcome.WasPlaintext ? "true" : "false",
                        ["source_relative_path"] = relativePath,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            artifacts.Add(descriptor);

            progress?.Report(new CaptureProgress
            {
                Stage = CaptureStages.Snapshotting,
                Processed = i + 1,
                Total = databases.Count,
                Detail = artifactName,
            });
        }

        progress?.Report(new CaptureProgress { Stage = CaptureStages.Finalizing });

        // If no artifact was captured at all, the snapshot has no usable evidence.
        if (artifacts.Count == 0)
        {
            return new SourceCaptureResult
            {
                Artifacts = [],
                Completeness = RawGenerationCompleteness.Incomplete,
                Diagnostics = [RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.PartitionUnreadable,
                    "No database could be materialized into a readable artifact; the capture has " +
                    "no preserved evidence.")],
            };
        }

        return new SourceCaptureResult
        {
            Artifacts = artifacts,
            Diagnostics = diagnostics,
            Completeness = completeness,
            SourceProductName = WeChatClient.ProductName,
            SourceVersion = clientVersion,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
