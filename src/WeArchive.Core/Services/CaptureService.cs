using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Services;

/// <summary>
/// Orchestrates a Raw Vault capture: source discovery -&gt; source-specific snapshot -&gt;
/// immutable generation publication. docs/RAW_VAULT.md, docs/ARCHITECTURE.md (Raw Vault
/// section), Issues #22 and #25.
/// <para>
/// This service is source-independent. It resolves the account through
/// <see cref="ISourceAdapter"/>, delegates artifact production to
/// <see cref="ISourceCaptureAdapter"/>, and publishes through <see cref="IRawVaultStore"/>.
/// It never touches WeChat files, key acquisition or SQLCipher directly.
/// </para>
/// <para>
/// Incremental capture (Issue #25): when the immediately preceding published generation is a
/// complete version-2 generation whose capture checkpoint matches this capture adapter, the
/// adapter's optional incremental path may reuse already-verified artifacts. The checkpoint
/// lives in the publish-last manifest, so it advances only when a complete generation is
/// actually published; a partial generation records coverage without advancing it, and a
/// failed or cancelled capture leaves the previous checkpoint unchanged. When no compatible
/// checkpoint exists, capture widens to a full consistent snapshot and reports
/// <see cref="DiagnosticCodes.CaptureFullFallback"/>. The capture checkpoint is independent of
/// canonical ingest progress (docs/DATA_MODEL.md section 21).
/// </para>
/// <para>
/// Source-partition policy (Issue #37): the checkpoint covers exactly the coverage entries this
/// generation recorded as <c>captured</c>/<c>reused</c>, i.e. the adapter's Required and Supported
/// auxiliary evidence. A <c>complete</c> generation may also carry <c>unsupported</c> entries for
/// discovered-but-unsupported partitions; those remain visible in coverage and are excluded from
/// the checkpoint. Independently of the adapter, a run that carries an <c>unavailable</c> coverage
/// entry or a partial-severity diagnostic is downgraded to <c>partial</c> and publishes no
/// checkpoint, so a coverage failure can never be reported as complete (docs/PRD.md FR-20).
/// </para>
/// <para>
/// Reliability — R1 (in-process publication): a normal success publishes exactly one complete
/// or partial generation. A Fatal diagnostic or caught cancellation/I/O failure discards the
/// staged material best-effort and publishes nothing, so an incomplete snapshot is never
/// mistaken for a complete generation. No persistent journal, commit marker or rollback
/// ledger is introduced (Issue #22/#25 non-goals).
/// </para>
/// </summary>
public sealed class CaptureService
{
    /// <summary>The manifest diagnostic severity that marks a partial-coverage finding.</summary>
    private const string PartialSeverity = "partial";

    private readonly ISourceAdapter _sourceAdapter;
    private readonly ISourceCaptureAdapter _captureAdapter;
    private readonly IRawVaultStore _vault;
    private readonly IClock _clock;

    public CaptureService(
        ISourceAdapter sourceAdapter,
        ISourceCaptureAdapter captureAdapter,
        IRawVaultStore vault,
        IClock clock)
    {
        _sourceAdapter = sourceAdapter ?? throw new ArgumentNullException(nameof(sourceAdapter));
        _captureAdapter = captureAdapter ?? throw new ArgumentNullException(nameof(captureAdapter));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>The capture adapter family, surfaced so the CLI can report it.</summary>
    public string CaptureAdapterFamily => _captureAdapter.CaptureAdapterFamily;

    /// <summary>The capture adapter version, surfaced so the CLI can report it.</summary>
    public string CaptureAdapterVersion => _captureAdapter.CaptureAdapterVersion;

    /// <summary>
    /// Captures one account into a versioned, immutable Raw Vault generation.
    /// </summary>
    public async Task<CaptureResult> CaptureAccountAsync(
        CaptureRequest request,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var descriptor = await _sourceAdapter
            .DescribeSourceAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!descriptor.IsAvailable)
        {
            return CaptureResult.Failed(
                string.Empty, string.Empty, request.SourceProfileId ?? string.Empty,
                _clock.UtcNow,
                descriptor.UnavailableReason ?? "The source is not available.");
        }

        var accounts = await _sourceAdapter
            .ListAccountsAsync(cancellationToken)
            .ConfigureAwait(false);

        var account = ResolveAccount(accounts, request.SourceProfileId);
        if (account is null)
        {
            return CaptureResult.Failed(
                string.Empty, string.Empty, request.SourceProfileId ?? string.Empty,
                _clock.UtcNow,
                "No source profile matched the requested account.");
        }

        var accountId = StableIds.Account(_sourceAdapter.AdapterName, account.SourceProfileId);
        var captureTime = _clock.UtcNow;

        // Link to the immediately preceding published generation so the chain is append-only.
        var previous = await _vault
            .GetLatestGenerationAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        var context = new RawGenerationContext
        {
            AccountId = accountId,
            SourceProfileId = account.SourceProfileId,
            CaptureTime = captureTime,
            CaptureAdapterFamily = _captureAdapter.CaptureAdapterFamily,
            CaptureAdapterVersion = _captureAdapter.CaptureAdapterVersion,
        };

        RawGeneration? reusable = null;
        var fallbackDiagnostics = new List<RawManifestDiagnostic>();
        if (previous is not null && _captureAdapter is IIncrementalSourceCaptureAdapter)
        {
            reusable = await _vault.OpenGenerationAsync(accountId, previous.GenerationId, cancellationToken)
                .ConfigureAwait(false);
            var checkpoint = reusable?.Manifest.CaptureCheckpoint;
            if (reusable is null || reusable.Manifest.Capture.Completeness != RawGenerationCompleteness.Complete ||
                checkpoint is null || checkpoint.Version != 1 ||
                checkpoint.GenerationId != previous.GenerationId ||
                checkpoint.CaptureAdapterFamily != _captureAdapter.CaptureAdapterFamily ||
                checkpoint.CaptureAdapterVersion != _captureAdapter.CaptureAdapterVersion)
            {
                reusable = null;
                fallbackDiagnostics.Add(RawManifestDiagnostic.Info(DiagnosticCodes.CaptureFullFallback,
                    "No compatible, verified capture checkpoint is available; the whole source was read."));
            }
        }

        var session = await _vault
            .BeginGenerationAsync(context, cancellationToken)
            .ConfigureAwait(false);

        SourceCaptureResult captureResult;
        try
        {
            captureResult = reusable is not null && _captureAdapter is IIncrementalSourceCaptureAdapter incremental
                ? await incremental.CaptureIncrementalAsync(account.SourceProfileId, session, reusable, progress, cancellationToken).ConfigureAwait(false)
                : await _captureAdapter.CaptureAsync(account.SourceProfileId, session, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DiscardAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await DiscardAsync(session).ConfigureAwait(false);
            return CaptureResult.Failed(
                session.GenerationId, accountId, account.SourceProfileId, captureTime, ex.Message);
        }

        // A Fatal diagnostic means required evidence is missing or the snapshot is inconsistent.
        // The generation must not be published as complete (Issue #22).
        var fatal = captureResult.Diagnostics
            .FirstOrDefault(d => string.Equals(d.Severity, "fatal", StringComparison.OrdinalIgnoreCase));

        if (fatal is not null)
        {
            await DiscardAsync(session).ConfigureAwait(false);
            return CaptureResult.Failed(
                session.GenerationId, accountId, account.SourceProfileId, captureTime,
                fatal.Message, captureResult.Diagnostics);
        }

        if (captureResult.Completeness == RawGenerationCompleteness.Incomplete)
        {
            await DiscardAsync(session).ConfigureAwait(false);
            var message = captureResult.Diagnostics
                .FirstOrDefault(d => string.Equals(d.Severity, "partial", StringComparison.OrdinalIgnoreCase))?.Message
                ?? "Capture did not achieve complete coverage.";
            return CaptureResult.Failed(
                session.GenerationId, accountId, account.SourceProfileId, captureTime,
                message, captureResult.Diagnostics);
        }

        var diagnostics = fallbackDiagnostics.Concat(captureResult.Diagnostics).ToArray();
        var completeness = captureResult.Completeness;

        // Defensive alignment with docs/PRD.md FR-20: a run whose coverage contains an
        // `unavailable` partition, or whose diagnostics contain a partial-severity finding, is
        // never published as `complete` even if the capture adapter claimed it was. The downgrade
        // is recorded, not silent, so the published verdict stays auditable.
        if (completeness == RawGenerationCompleteness.Complete &&
            (captureResult.Coverage.Any(c => c.Status == RawPartitionStatus.Unavailable) ||
                diagnostics.Any(d =>
                    string.Equals(d.Severity, PartialSeverity, StringComparison.OrdinalIgnoreCase))))
        {
            diagnostics =
            [
                .. diagnostics,
                RawManifestDiagnostic.Partial(
                    DiagnosticCodes.CaptureCompletenessDowngraded,
                    "The capture adapter reported complete coverage, but this run carries an " +
                    "unavailable partition or a partial diagnostic; the generation was recorded " +
                    "as partial."),
            ];
            completeness = RawGenerationCompleteness.Partial;
        }

        // The checkpoint covers exactly the captured/reused evidence of this generation. A
        // complete generation may legitimately also carry `unsupported` coverage entries
        // (docs/RAW_VAULT.md "Source-partition support policy", Issue #37); those are accounted
        // for in coverage but are not checkpoint evidence and are never reusable.
        var checkpointCoverage = captureResult.Coverage
            .Where(c => c.Status is RawPartitionStatus.Captured or RawPartitionStatus.Reused)
            .ToArray();

        var checkpointValue = completeness == RawGenerationCompleteness.Complete &&
            checkpointCoverage.Length > 0 &&
            checkpointCoverage.All(c =>
                !string.IsNullOrWhiteSpace(c.SourceFingerprint) &&
                !string.IsNullOrWhiteSpace(c.ArtifactSha256) &&
                captureResult.Artifacts.Any(a => a.Sha256 == c.ArtifactSha256))
            ? new RawCaptureCheckpoint
            {
                GenerationId = session.GenerationId,
                CaptureAdapterFamily = _captureAdapter.CaptureAdapterFamily,
                CaptureAdapterVersion = _captureAdapter.CaptureAdapterVersion,
                PartitionFingerprints = checkpointCoverage.ToDictionary(
                    c => c.PartitionId, c => c.SourceFingerprint!, StringComparer.Ordinal),
            }
            : null;

        var manifest = new RawManifest
        {
            ManifestVersion = session.ManifestVersion,
            VaultFormatVersion = session.VaultFormatVersion,
            GenerationId = session.GenerationId,
            AccountId = accountId,
            SourceProfileId = account.SourceProfileId,
            Source = new RawManifestSource
            {
                AdapterName = _sourceAdapter.AdapterName,
                AdapterVersion = _sourceAdapter.AdapterVersion,
                SourceProductName = captureResult.SourceProductName ?? descriptor.SourceProductName,
                SourceVersion = captureResult.SourceVersion ?? descriptor.SourceVersion,
            },
            Capture = new RawManifestCapture
            {
                CaptureTime = captureTime,
                CaptureAdapterFamily = _captureAdapter.CaptureAdapterFamily,
                CaptureAdapterVersion = _captureAdapter.CaptureAdapterVersion,
                Mode = captureResult.Mode,
                Completeness = completeness,
                ArtifactCount = captureResult.Artifacts.Count,
            },
            Artifacts = captureResult.Artifacts,
            Diagnostics = diagnostics,
            Coverage = captureResult.Coverage,
            CaptureCheckpoint = checkpointValue,
            PreviousGenerationId = previous?.GenerationId,
        };

        try
        {
            await session.PublishAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DiscardAsync(session).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await DiscardAsync(session).ConfigureAwait(false);
            return CaptureResult.Failed(
                session.GenerationId, accountId, account.SourceProfileId, captureTime, ex.Message);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        return new CaptureResult
        {
            StorageCounters = session.StorageCounters,
            Succeeded = true,
            GenerationId = session.GenerationId,
            AccountId = accountId,
            SourceProfileId = account.SourceProfileId,
            CaptureTime = captureTime,
            Completeness = completeness,
            ArtifactCount = captureResult.Artifacts.Count,
            Diagnostics = diagnostics,
            Coverage = captureResult.Coverage,
            Mode = captureResult.Mode,
            PreviousGenerationId = previous?.GenerationId,
        };
    }

    private static SourceAccount? ResolveAccount(
        IReadOnlyList<SourceAccount> accounts,
        string? requestedProfileId)
    {
        if (accounts.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedProfileId))
        {
            return accounts.FirstOrDefault(a =>
                string.Equals(a.SourceProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase));
        }

        return accounts.FirstOrDefault(a => a.IsCurrent) ?? accounts[0];
    }

    /// <summary>Best-effort discard that never throws, so a failure path stays a failure.</summary>
    private static async Task DiscardAsync(IRawGenerationSession session)
    {
        try
        {
            await session.DiscardAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Discard is best-effort; a failure here must not mask the original failure.
        }
    }
}
