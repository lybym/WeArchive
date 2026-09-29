using WeArchive.Core.Domain;

namespace WeArchive.Core.RawVault;

/// <summary>
/// Overall completeness verdict of one canonical read over verified evidence. It answers the
/// canonical question — was the evidence required by this canonical result actually available —
/// and is deliberately not the capture-side acquisition story
/// (docs/RAW_VAULT.md section 4.3, Issue #51).
/// </summary>
public enum CanonicalCoverageVerdict
{
    /// <summary>
    /// Every supported evidence domain the verified generation accounts for was available and
    /// read; no required evidence was unavailable and no unclassified evidence remains.
    /// </summary>
    Complete,

    /// <summary>
    /// Required evidence was missing, unreadable or unclassifiable — or the generation itself was
    /// not verified complete — so the canonical result is not proven complete and the R2 ingest
    /// path refuses to publish it as one.
    /// </summary>
    Incomplete,
}

/// <summary>
/// The source-neutral canonical coverage rollup of one verified Raw Vault generation: a
/// deterministic application/domain-level summary of whether the supported evidence required by
/// a canonical result was available, and what was explicitly not
/// (docs/PRD.md G2/G3/FR-13/FR-20/NFR-05, docs/RAW_VAULT.md section 4.3, Issue #51).
/// <para>
/// The model is derived only from the persisted manifest (completeness verdict, coverage entries
/// and diagnostics) — never from WeChat tables, Raw Vault paths, checkpoint JSON or keys. The
/// capture-side <c>captured</c> versus <c>reused</c> split is acquisition metadata and is merged
/// into <see cref="Available"/>: canonical coverage answers whether the evidence was available
/// for the canonical operation, not how it was reacquired. It is likewise distinct from the
/// ingest-progress <c>conversation_coverage</c> checkpoint cursor, which records that a newer
/// generation was verified unchanged and must never be read as a completeness statement
/// (docs/DATA_MODEL.md section 14.1).
/// </para>
/// <para>
/// Known-unsupported and unknown/unclassified evidence are distinguished by the diagnostic codes
/// the source-partition policy already records (<c>partition_unsupported</c> info versus
/// <c>partition_unclassified</c> partial, Issue #37), so the rollup never re-classifies source
/// partitions outside the source/preservation boundary. An unsupported entry whose diagnostic
/// cannot be attributed to either code is counted as unclassified: unknown evidence stays
/// conservative and can never silently produce a complete verdict.
/// </para>
/// </summary>
public sealed record CanonicalCoverage
{
    /// <summary>Whether the canonical result's evidence completeness is proven.</summary>
    public required CanonicalCoverageVerdict Verdict { get; init; }

    /// <summary>
    /// Every evidence domain the verified generation accounts for — the same rule as the capture
    /// contract's <c>expected</c>: coverage entries of this run, not a claim that every
    /// theoretical source partition was observed.
    /// </summary>
    public required int Expected { get; init; }

    /// <summary>Expected supported evidence that was available and read for the canonical result (<c>captured</c> plus <c>reused</c>).</summary>
    public required int Available { get; init; }

    /// <summary>Expected supported evidence that could not be read or was absent from the source.</summary>
    public required int Unavailable { get; init; }

    /// <summary>
    /// Discovered evidence explicitly classified outside the adapter's supported contract
    /// (<c>partition_unsupported</c>); it stays visible but does not by itself prevent
    /// supported completeness.
    /// </summary>
    public required int KnownUnsupported { get; init; }

    /// <summary>
    /// Evidence without an approved support classification (<c>partition_unclassified</c>) or
    /// that cannot be attributed to a policy diagnostic. Unclassified evidence is conservative:
    /// its presence keeps the verdict incomplete.
    /// </summary>
    public required int Unclassified { get; init; }

    /// <summary>Rolls one published generation's manifest up deterministically.</summary>
    public static CanonicalCoverage From(RawManifest manifest) =>
        From(manifest.Capture.Completeness, manifest.Coverage, manifest.Diagnostics);

    /// <summary>
    /// Rolls the same manifest data up from its parts, so a caller holding the just-published
    /// capture result computes exactly the rollup the persisted manifest produces.
    /// </summary>
    public static CanonicalCoverage From(
        RawGenerationCompleteness completeness,
        IReadOnlyList<RawPartitionCoverage> coverage,
        IReadOnlyList<RawManifestDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var knownUnsupportedMessages = MessagesOf(diagnostics, DiagnosticCodes.PartitionUnsupported);
        var unclassifiedMessages = MessagesOf(diagnostics, DiagnosticCodes.PartitionUnclassified);

        var available = 0;
        var unavailable = 0;
        var knownUnsupported = 0;
        var unclassified = 0;
        foreach (var entry in coverage)
        {
            switch (entry.Status)
            {
                case RawPartitionStatus.Captured:
                case RawPartitionStatus.Reused:
                    available++;
                    break;
                case RawPartitionStatus.Unavailable:
                    unavailable++;
                    break;
                case RawPartitionStatus.Unsupported:
                    if (entry.Diagnostic is not null && knownUnsupportedMessages.Contains(entry.Diagnostic))
                    {
                        knownUnsupported++;
                    }
                    else
                    {
                        // Explicitly unclassified, or unsupported evidence no policy diagnostic
                        // accounts for: both stay conservative.
                        unclassified++;
                    }

                    break;
            }
        }

        return new CanonicalCoverage
        {
            // The verdict mirrors the generation's own completeness rule — only a generation
            // whose every required artifact was captured/reused and verified may claim a complete
            // canonical read — and stays conservative: unknown/unclassified evidence can never
            // silently produce a complete verdict even if a manifest claimed one
            // (docs/PRD.md FR-20). Through the shipped capture policy a complete generation never
            // carries unavailable or unclassified evidence (both force partial), so every
            // canonical result the ingest path publishes is complete.
            Verdict = completeness == RawGenerationCompleteness.Complete
                && unavailable == 0
                && unclassified == 0
                ? CanonicalCoverageVerdict.Complete
                : CanonicalCoverageVerdict.Incomplete,
            Expected = coverage.Count,
            Available = available,
            Unavailable = unavailable,
            KnownUnsupported = knownUnsupported,
            Unclassified = unclassified,
        };
    }

    private static HashSet<string> MessagesOf(IReadOnlyList<RawManifestDiagnostic> diagnostics, string code) =>
        [.. diagnostics
            .Where(d => string.Equals(d.Code, code, StringComparison.Ordinal))
            .Select(d => d.Message)
            .Where(m => m is not null)
            .Select(m => m!)];
}

/// <summary>
/// A published Raw Vault generation whose evidence coverage is not complete cannot establish a
/// complete canonical read, so the R2 ingest path refuses it instead of publishing a canonical
/// result that could be mistaken for a complete one
/// (docs/PRD.md FR-20, docs/DEVELOPMENT.md section 10.4, Issue #51).
/// <para>
/// It carries the source-neutral <see cref="CanonicalCoverage"/> rollup of the rejected
/// generation so a machine caller can distinguish an incomplete read from any other failure
/// without inspecting manifests, paths or checkpoints. The fail-closed handling is unchanged:
/// every caller already treated a non-complete generation as a hard data failure, and the
/// coverage detail is additive. (<see cref="InvalidDataException"/> is sealed on .NET 10, so
/// this is a direct <see cref="Exception"/>.)
/// </para>
/// </summary>
public sealed class IncompleteCanonicalCoverageException : Exception
{
    /// <summary>The deterministic coverage rollup of the generation that was refused.</summary>
    public CanonicalCoverage Coverage { get; }

    public IncompleteCanonicalCoverageException(CanonicalCoverage coverage, string message)
        : base(message)
    {
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
    }
}
