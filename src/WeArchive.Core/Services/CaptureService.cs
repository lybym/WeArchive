using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Services;

/// <summary>
/// Orchestrates a Raw Vault baseline capture: source discovery -&gt; source-specific
/// snapshot -&gt; immutable generation publication. docs/RAW_VAULT.md, docs/ARCHITECTURE.md
/// (Raw Vault section), Issue #22.
/// <para>
/// This service is source-independent. It resolves the account through
/// <see cref="ISourceAdapter"/>, delegates artifact production to
/// <see cref="ISourceCaptureAdapter"/>, and publishes through <see cref="IRawVaultStore"/>.
/// It never touches WeChat files, key acquisition or SQLCipher directly.
/// </para>
/// <para>
/// Reliability — R1 (in-process publication): a normal success publishes exactly one complete
/// generation. A Fatal diagnostic or caught cancellation/I/O failure discards the staged
/// material best-effort and publishes nothing, so an incomplete snapshot is never mistaken
/// for a complete generation. No persistent journal, commit marker or rollback ledger is
/// introduced (Issue #22 non-goals).
/// </para>
/// </summary>
public sealed class CaptureService
{
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

        var session = await _vault
            .BeginGenerationAsync(context, cancellationToken)
            .ConfigureAwait(false);

        SourceCaptureResult captureResult;
        try
        {
            captureResult = await _captureAdapter
                .CaptureAsync(account.SourceProfileId, session, progress, cancellationToken)
                .ConfigureAwait(false);
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

        var manifest = new RawManifest
        {
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
                Mode = RawCaptureMode.Baseline,
                Completeness = captureResult.Completeness,
                ArtifactCount = captureResult.Artifacts.Count,
            },
            Artifacts = captureResult.Artifacts,
            Diagnostics = captureResult.Diagnostics,
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
            Succeeded = true,
            GenerationId = session.GenerationId,
            AccountId = accountId,
            SourceProfileId = account.SourceProfileId,
            CaptureTime = captureTime,
            Completeness = captureResult.Completeness,
            ArtifactCount = captureResult.Artifacts.Count,
            Diagnostics = captureResult.Diagnostics,
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
