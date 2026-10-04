using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
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
/// <para>
/// Source-partition support policy (Issue #37, docs/RAW_VAULT.md): filesystem discovery does not
/// define product support. Every discovered partition is classified by
/// <see cref="WeChatSourcePartitionPolicy"/> as Required, Supported auxiliary, Known unsupported or
/// Unknown. Only Required/Supported-auxiliary partitions are materialized, fingerprinted and made
/// addressable by the capture checkpoint; Known-unsupported evidence stays visible as
/// <c>unsupported</c> coverage without downgrading <c>Complete</c>; Unknown evidence additionally
/// forces <c>Partial</c> so an unclassified partition can never be silently reported as covered.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WeChatCaptureAdapter : IIncrementalSourceCaptureAdapter, IDisposable
{
    public const string Family = "wechat-windows";

    /// <summary>
    /// This version identifies the source-consistency proof used to build capture checkpoints.
    /// It advances whenever validation semantics change, so older checkpoints cannot let a newly
    /// validated source partition bypass full materialization.
    /// </summary>
    public const string Version = "0.3.0";

    private const string SourceDatabaseRole = "source-database";

    private readonly IWeChatDatabaseKeyAcquirer _keyAcquirer;
    private readonly IWeChatCaptureEnvironment _environment;
    private bool _disposed;

    public WeChatCaptureAdapter()
        : this(new WcdbCipherConfigKeyAcquirer(), new WeChatCaptureEnvironment())
    {
    }

    internal WeChatCaptureAdapter(IWeChatDatabaseKeyAcquirer keyAcquirer)
        : this(keyAcquirer, new WeChatCaptureEnvironment())
    {
    }

    /// <summary>
    /// Test seam: the live-source facts and the materialization step are supplied rather than
    /// reached through static discovery, so this shipped decision logic can be exercised against
    /// a fixture source without a running client or a real database key (docs/PRD.md NFR-06).
    /// </summary>
    internal WeChatCaptureAdapter(IWeChatDatabaseKeyAcquirer keyAcquirer, IWeChatCaptureEnvironment environment)
    {
        _keyAcquirer = keyAcquirer ?? throw new ArgumentNullException(nameof(keyAcquirer));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public string CaptureAdapterFamily => Family;

    public string CaptureAdapterVersion => Version;

    public async Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken) =>
        await CaptureCoreAsync(sourceProfileId, session, null, progress, cancellationToken)
            .ConfigureAwait(false);

    public async Task<SourceCaptureResult> CaptureIncrementalAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        RawGeneration previous,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return await CaptureCoreAsync(sourceProfileId, session, previous, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SourceCaptureResult> CaptureCoreAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        RawGeneration? previous,
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
        var account = _environment
            .DiscoverAccounts()
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

        // A `-wal`/`-shm` sibling is fingerprint input, not a partition, so it is never classified.
        var databases = _environment
            .EnumerateDatabases(account)
            .Where(WeChatSourcePartitionPolicy.IsPartitionFile)
            .ToArray();
        if (databases.Length == 0)
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

        var diagnostics = new List<RawManifestDiagnostic>();
        var artifacts = new List<RawArtifactDescriptor>();
        var coverage = new List<RawPartitionCoverage>();
        var completeness = RawGenerationCompleteness.Complete;
        var clientVersion = _environment.DetectClientVersion();
        var current = databases
            .Select(path => new SourcePartition(
                Path.GetRelativePath(account.DatabaseDirectory, path).Replace('\\', '/'), path))
            .OrderBy(partition => partition.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Classify every discovered partition exactly once, in a deterministic order. Discovery
        // alone never makes a partition required evidence (docs/RAW_VAULT.md "Source-partition
        // support policy", Issue #37): only positively classified Required/Supported-auxiliary
        // partitions are materialized, fingerprinted and addressable by the capture checkpoint.
        var partitionClasses = new Dictionary<string, WeChatSourcePartitionClass>(StringComparer.OrdinalIgnoreCase);
        foreach (var partition in current)
        {
            partitionClasses[partition.Id] = WeChatSourcePartitionPolicy.Classify(partition.Id);
        }

        var evidence = current
            .Where(partition => WeChatSourcePartitionPolicy.IsSupportedEvidence(partitionClasses[partition.Id]))
            .ToArray();

        // A verified prior generation is still insufficient without a matching capture cursor
        // and unambiguous path-to-artifact mapping. In that case do a fresh full capture.
        var prior = BuildPriorMap(previous);
        if (previous is not null && prior is null)
        {
            diagnostics.Add(RawManifestDiagnostic.Info(
                DiagnosticCodes.CaptureFullFallback,
                "Prior partition evidence cannot be mapped to this adapter's partitions; the whole source was read."));
            previous = null;
        }

        var before = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var partition in evidence)
            {
                before.Add(partition.Id, await FingerprintDatabaseAsync(partition.Path, cancellationToken)
                    .ConfigureAwait(false));
            }
        }
        catch (IOException) when (previous is not null)
        {
            var fallback = await CaptureCoreAsync(sourceProfileId, session, null, progress, cancellationToken)
                .ConfigureAwait(false);
            return fallback with
            {
                Diagnostics =
                [
                    RawManifestDiagnostic.Info(DiagnosticCodes.CaptureFullFallback,
                        "A source database changed while its preflight fingerprint was read; the whole source was read."),
                    .. fallback.Diagnostics,
                ],
            };
        }

        if (previous is not null)
        {
            foreach (var missing in prior!.Keys.Except(current.Select(p => p.Id), StringComparer.OrdinalIgnoreCase)
                         .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                var message = $"Previously captured partition '{missing}' is unavailable in the live source.";
                diagnostics.Add(RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionMissing, message));
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = missing,
                    Status = RawPartitionStatus.Unavailable,
                    Diagnostic = message,
                });
                completeness = RawGenerationCompleteness.Partial;
            }
        }

        // Only supported evidence decides whether anything must be materialized: an unsupported or
        // unclassified partition is never read and never needs a key.
        var needsCapture = previous is null || evidence.Any(p =>
            !prior!.TryGetValue(p.Id, out var old) ||
            !string.Equals(old.Fingerprint, before[p.Id], StringComparison.Ordinal));

        // Acquire a transient key only if there is new or changed evidence to materialize.
        WeChatKeySet? keySet = null;
        if (needsCapture && evidence.Length > 0 && !_environment.IsClientRunning())
        {
            return new SourceCaptureResult
            {
                Artifacts = [],
                Completeness = RawGenerationCompleteness.Incomplete,
                Coverage = coverage,
                Mode = previous is null ? RawCaptureMode.Baseline : RawCaptureMode.Incremental,
                Diagnostics = [RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.KeyAcquisitionFailed,
                    "WeChat is not running. The local database key can only be recovered while " +
                    "the client is running and signed in.")],
            };
        }

        if (needsCapture && evidence.Length > 0)
        {
            // The acquirer uses this list only to verify candidate keys against real database
            // pages, so narrowing it to the partitions the adapter will materialize is safe.
            var keyResult = _keyAcquirer.Acquire([.. evidence.Select(partition => partition.Path)]);
            if (!keyResult.Succeeded || keyResult.KeySet is null)
            {
                return new SourceCaptureResult
                {
                    Artifacts = [],
                    Completeness = RawGenerationCompleteness.Incomplete,
                    Coverage = coverage,
                    Mode = previous is null ? RawCaptureMode.Baseline : RawCaptureMode.Incremental,
                    Diagnostics = [RawManifestDiagnostic.Fatal(
                        DiagnosticCodes.KeyAcquisitionFailed, keyResult.Message)],
                };
            }
            keySet = keyResult.KeySet;
        }

        using var materializer = keySet is null ? null : _environment.CreateMaterializer(keySet);

        progress?.Report(new CaptureProgress
        {
            Stage = CaptureStages.Snapshotting,
            Total = current.Length,
            Detail = sourceProfileId,
        });

        for (var i = 0; i < current.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partition = current[i];
            var partitionClass = partitionClasses[partition.Id];

            // Known-unsupported evidence is accounted for, not silently skipped and not required:
            // it is never materialized, never fingerprinted and never addressable by the
            // checkpoint, and its presence alone does not downgrade the generation.
            if (partitionClass is WeChatSourcePartitionClass.KnownUnsupported)
            {
                var unsupportedMessage =
                    $"Source partition '{partition.Id}' is outside this adapter version's " +
                    "supported evidence contract; it is recorded as unsupported and is not " +
                    "required for a complete capture.";
                diagnostics.Add(RawManifestDiagnostic.Info(
                    DiagnosticCodes.PartitionUnsupported, unsupportedMessage));
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partition.Id,
                    Status = RawPartitionStatus.Unsupported,
                    Diagnostic = unsupportedMessage,
                });
                progress?.Report(new CaptureProgress
                {
                    Stage = CaptureStages.Snapshotting,
                    Processed = i + 1,
                    Total = current.Length,
                    Detail = partition.Id,
                });
                continue;
            }

            // Unclassified evidence is conservative: it stays visible as unsupported coverage and
            // forces Partial, so the run can never be reported Complete and never publishes a
            // checkpoint until the partition's product semantics are classified.
            if (partitionClass is WeChatSourcePartitionClass.Unknown)
            {
                var unclassifiedMessage =
                    $"Source partition '{partition.Id}' has no approved support classification for " +
                    "this adapter version; it is recorded as unsupported and the generation cannot " +
                    "be complete until that partition is classified.";
                diagnostics.Add(RawManifestDiagnostic.Partial(
                    DiagnosticCodes.PartitionUnclassified, unclassifiedMessage));
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partition.Id,
                    Status = RawPartitionStatus.Unsupported,
                    Diagnostic = unclassifiedMessage,
                });
                completeness = RawGenerationCompleteness.Partial;
                progress?.Report(new CaptureProgress
                {
                    Stage = CaptureStages.Snapshotting,
                    Processed = i + 1,
                    Total = current.Length,
                    Detail = partition.Id,
                });
                continue;
            }

            var databasePath = partition.Path;
            var fingerprint = before[partition.Id];

            if (previous is not null && prior!.TryGetValue(partition.Id, out var old) &&
                string.Equals(old.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                var reused = await session.ReuseArtifactAsync(previous, old.Artifact, cancellationToken)
                    .ConfigureAwait(false);
                artifacts.Add(reused);
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partition.Id,
                    Status = RawPartitionStatus.Reused,
                    SourceFingerprint = fingerprint,
                    ArtifactSha256 = reused.Sha256,
                });
                progress?.Report(new CaptureProgress
                {
                    Stage = CaptureStages.Snapshotting,
                    Processed = i + 1,
                    Total = current.Length,
                    Detail = reused.Name,
                });
                continue;
            }

            DecryptionOutcome outcome;
            try
            {
                outcome = materializer!.GetPlaintext(databasePath);
            }
            catch (WeChatKeyUnavailableException ex)
            {
                // A specific database whose key could not be resolved is partial coverage,
                // not a fatal abort: the rest of the account may still be captured.
                diagnostics.Add(RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnreadable, ex.Message));
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partition.Id,
                    Status = RawPartitionStatus.Unavailable,
                    Diagnostic = ex.Message,
                });
                completeness = RawGenerationCompleteness.Partial;
                progress?.Report(new CaptureProgress
                {
                    Stage = CaptureStages.Snapshotting,
                    Processed = i + 1,
                    Total = current.Length,
                });
                continue;
            }

            // Any malformed/torn WAL evidence prevents a Complete verdict, even when a valid
            // committed prefix could still be materialized.
            if (outcome.WalFramesRejected > 0)
            {
                diagnostics.Add(RawManifestDiagnostic.Partial(
                    DiagnosticCodes.WalFramesRejected,
                    $"{Path.GetFileName(databasePath)}: detected {outcome.WalFramesRejected} " +
                    "invalid or incomplete WAL evidence; only verified committed frames were " +
                    "materialized and the partition is not eligible for Complete coverage."));
                if (completeness == RawGenerationCompleteness.Complete)
                {
                    completeness = RawGenerationCompleteness.Partial;
                }
            }

            var artifactName = Path.GetFileName(databasePath);
            var relativePath = partition.Id;

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
            coverage.Add(new RawPartitionCoverage
            {
                PartitionId = partition.Id,
                Status = RawPartitionStatus.Captured,
                SourceFingerprint = fingerprint,
                ArtifactSha256 = descriptor.Sha256,
            });

            progress?.Report(new CaptureProgress
            {
                Stage = CaptureStages.Snapshotting,
                Processed = i + 1,
                Total = current.Length,
                Detail = artifactName,
            });
        }

        // Recheck all source evidence after reuse and decryption. Any change means neither the
        // copied old artifact nor the new snapshot can be claimed as the current source state.
        foreach (var partition in evidence)
        {
            var after = await FingerprintDatabaseAsync(partition.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(before[partition.Id], after, StringComparison.Ordinal))
            {
                diagnostics.Add(RawManifestDiagnostic.Fatal(
                    DiagnosticCodes.PartitionUnreadable,
                    $"Source partition '{partition.Id}' changed during capture; retry for a stable snapshot."));
                completeness = RawGenerationCompleteness.Incomplete;
            }
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
            Coverage = coverage,
            Mode = previous is null ? RawCaptureMode.Baseline : RawCaptureMode.Incremental,
            Completeness = completeness,
            SourceProductName = WeChatClient.ProductName,
            SourceVersion = clientVersion,
        };
    }

    /// <summary>
    /// Maps the live partitions of a verified predecessor generation onto the artifacts that
    /// already preserve them. Returns null when the predecessor cannot by itself prove that
    /// reusing its evidence is safe, in which case the caller widens to a full snapshot.
    /// <para>
    /// The safety preconditions are enforced here rather than trusted from the caller, so the
    /// adapter stays correct even when it is invoked with a predecessor the service would have
    /// rejected: the manifest must be version 2 or newer, the generation must be
    /// <see cref="RawGenerationCompleteness.Complete"/>, the checkpoint must name this exact
    /// generation with the same adapter family/version, and every covered partition must map
    /// unambiguously to exactly one artifact of the same generation whose checksum matches.
    /// </para>
    /// <para>
    /// The returned map is exactly the predecessor's checkpoint evidence, i.e. its
    /// <c>captured</c>/<c>reused</c> partitions. <c>unsupported</c> and <c>unavailable</c>
    /// coverage entries are accounted for in coverage but are not checkpoint evidence
    /// (docs/RAW_VAULT.md, Issue #37): reusing them would present evidence the adapter never
    /// preserved as unchanged source state.
    /// </para>
    /// </summary>
    internal static Dictionary<string, WeChatPriorPartition>? BuildPriorMap(RawGeneration? previous)
    {
        if (previous is null)
        {
            return null;
        }

        var checkpoint = previous.Manifest.CaptureCheckpoint;
        if (previous.Manifest.ManifestVersion < 2 || checkpoint is null ||
            checkpoint.Version != 1 || checkpoint.GenerationId != previous.GenerationId ||
            checkpoint.CaptureAdapterFamily != Family || checkpoint.CaptureAdapterVersion != Version)
        {
            return null;
        }

        // A partial predecessor cannot establish that its coverage is still current, so its
        // evidence is never presented as unchanged source state.
        if (previous.Manifest.Capture.Completeness != RawGenerationCompleteness.Complete)
        {
            return null;
        }

        var artifacts = new Dictionary<string, RawArtifactDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in previous.Manifest.Artifacts)
        {
            if (artifact.Metadata?.TryGetValue("source_relative_path", out var path) != true ||
                string.IsNullOrWhiteSpace(path) || !artifacts.TryAdd(path.Replace('\\', '/'), artifact))
            {
                return null;
            }
        }

        var result = new Dictionary<string, WeChatPriorPartition>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in previous.Manifest.Coverage)
        {
            if (item.Status is not (RawPartitionStatus.Captured or RawPartitionStatus.Reused))
            {
                // Only captured/reused evidence is addressable by the checkpoint; a non-evidence
                // entry that appears in it would make the predecessor's meaning ambiguous.
                if (checkpoint.PartitionFingerprints.ContainsKey(item.PartitionId))
                {
                    return null;
                }

                continue;
            }

            if (item.SourceFingerprint is null || item.ArtifactSha256 is null ||
                !checkpoint.PartitionFingerprints.TryGetValue(item.PartitionId, out var fingerprint) ||
                !string.Equals(fingerprint, item.SourceFingerprint, StringComparison.Ordinal) ||
                !artifacts.TryGetValue(item.PartitionId, out var artifact) ||
                !string.Equals(artifact.Sha256, item.ArtifactSha256, StringComparison.Ordinal) ||
                !result.TryAdd(item.PartitionId, new WeChatPriorPartition(fingerprint, artifact)))
            {
                return null;
            }
        }

        return result.Count == artifacts.Count && result.Count == checkpoint.PartitionFingerprints.Count
            ? result : null;
    }

    internal static async Task<string> FingerprintDatabaseAsync(string databasePath, CancellationToken cancellationToken)
    {
        // The WAL is authoritative SQLite evidence. The volatile -shm index is not.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("wechat-db-wal-v1\0"));
        foreach (var (suffix, required) in new[] { ("", true), ("-wal", false) })
        {
            var path = databasePath + suffix;
            hash.AppendData(Encoding.UTF8.GetBytes(suffix));
            hash.AppendData([0]);
            if (!File.Exists(path) && !required)
            {
                hash.AppendData([0]);
                continue;
            }

            hash.AppendData([1]);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var length = stream.Length;
            hash.AppendData(BitConverter.GetBytes(length));
            var contentHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            hash.AppendData(contentHash);
            if (stream.Length != length)
            {
                throw new IOException("Source database or WAL changed while computing its fingerprint.");
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record SourcePartition(string Id, string Path);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}

/// <summary>
/// One partition of a verified predecessor generation that this adapter may present as
/// unchanged source state: the fingerprint recorded for it and the artifact that preserves it.
/// </summary>
internal sealed record WeChatPriorPartition(string Fingerprint, RawArtifactDescriptor Artifact);
