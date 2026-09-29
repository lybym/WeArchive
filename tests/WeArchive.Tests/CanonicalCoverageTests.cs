using System.Text.Json;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Tests;

/// <summary>
/// Unit tests for the source-neutral canonical coverage rollup (Issue #51): the deterministic
/// mapping from a verified Raw Vault generation's manifest — completeness verdict, coverage
/// entries and policy diagnostics — onto the application-level completeness contract
/// (docs/PRD.md G2/G3/FR-13/FR-20/NFR-05, docs/RAW_VAULT.md section 4.3).
/// <para>
/// Coverage entry ids are opaque strings on purpose: the rollup must never classify source
/// partitions itself, only read the diagnostic codes the source-partition policy already
/// recorded (Issue #37).
/// </para>
/// </summary>
public sealed class CanonicalCoverageTests
{
    // ---- rollup semantics ----------------------------------------------------

    [Fact]
    public void AllAvailableEvidenceRollsUpComplete()
    {
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Captured("evidence/messages-b"),
            Reused("evidence/sessions"),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Complete, coverage, diagnostics: []);

        Assert.Equal(CanonicalCoverageVerdict.Complete, rollup.Verdict);
        Assert.Equal(3, rollup.Expected);
        Assert.Equal(3, rollup.Available);
        Assert.Equal(0, rollup.Unavailable);
        Assert.Equal(0, rollup.KnownUnsupported);
        Assert.Equal(0, rollup.Unclassified);
    }

    [Fact]
    public void OneUnavailableEvidenceCannotRollUpComplete()
    {
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Captured("evidence/sessions"),
            Unavailable("evidence/messages-b"),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Partial, coverage, diagnostics: []);

        Assert.Equal(CanonicalCoverageVerdict.Incomplete, rollup.Verdict);
        Assert.Equal(3, rollup.Expected);
        Assert.Equal(2, rollup.Available);
        Assert.Equal(1, rollup.Unavailable);
    }

    [Fact]
    public void KnownUnsupportedEvidenceStaysExplicitWithoutBlockingCompleteness()
    {
        const string reason = "The staging partition is outside the supported evidence contract.";
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Captured("evidence/sessions"),
            Unsupported("evidence/staging", reason),
        };
        var diagnostics = new[]
        {
            RawManifestDiagnostic.Info(DiagnosticCodes.PartitionUnsupported, reason),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Complete, coverage, diagnostics);

        // Known-unsupported evidence is accounted for explicitly, but its presence alone does not
        // prevent a supported generation from being complete (docs/RAW_VAULT.md section 4.3).
        Assert.Equal(CanonicalCoverageVerdict.Complete, rollup.Verdict);
        Assert.Equal(3, rollup.Expected);
        Assert.Equal(2, rollup.Available);
        Assert.Equal(1, rollup.KnownUnsupported);
        Assert.Equal(0, rollup.Unclassified);
    }

    [Fact]
    public void UnclassifiedEvidenceCannotProduceACompleteVerdict()
    {
        const string reason = "The new partition has no approved support classification.";
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Captured("evidence/sessions"),
            Unsupported("evidence/newdb", reason),
        };
        var diagnostics = new[]
        {
            RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnclassified, reason),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Partial, coverage, diagnostics);

        // Unknown/unclassified evidence is conservative and can never silently produce a
        // complete verdict (Issue #51).
        Assert.Equal(CanonicalCoverageVerdict.Incomplete, rollup.Verdict);
        Assert.Equal(1, rollup.Unclassified);
        Assert.Equal(0, rollup.KnownUnsupported);
    }

    [Fact]
    public void ACompleteManifestClaimingUnclassifiedEvidenceStillRollsUpIncomplete()
    {
        // Defensive: a manifest that claims complete while carrying unattributable unsupported
        // evidence cannot silently report a complete canonical read.
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Unsupported("evidence/unattributed"),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Complete, coverage, diagnostics: []);

        Assert.Equal(CanonicalCoverageVerdict.Incomplete, rollup.Verdict);
        Assert.Equal(1, rollup.Unclassified);
    }

    [Fact]
    public void MixedEvidenceRollsUpEveryBucket()
    {
        const string knownReason = "Known unsupported partition.";
        const string unknownReason = "Unclassified partition.";
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Captured("evidence/messages-b"),
            Reused("evidence/sessions"),
            Unavailable("evidence/contacts"),
            Unsupported("evidence/staging", knownReason),
            Unsupported("evidence/newdb", unknownReason),
        };
        var diagnostics = new[]
        {
            RawManifestDiagnostic.Info(DiagnosticCodes.PartitionUnsupported, knownReason),
            RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnclassified, unknownReason),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Partial, coverage, diagnostics);

        Assert.Equal(CanonicalCoverageVerdict.Incomplete, rollup.Verdict);
        Assert.Equal(6, rollup.Expected);
        Assert.Equal(3, rollup.Available);
        Assert.Equal(1, rollup.Unavailable);
        Assert.Equal(1, rollup.KnownUnsupported);
        Assert.Equal(1, rollup.Unclassified);
    }

    [Fact]
    public void UnsupportedEvidenceWithoutAPolicyDiagnosticIsCountedConservatively()
    {
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Unsupported("evidence/undiagnosed"),
            Unsupported("evidence/other-code", "some unrelated engineering note"),
        };
        var diagnostics = new[]
        {
            RawManifestDiagnostic.Info(DiagnosticCodes.CaptureFullFallback, "some unrelated engineering note"),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Complete, coverage, diagnostics);

        // Neither entry can be attributed to the known-unsupported policy diagnostic, so both
        // stay in the conservative unclassified bucket.
        Assert.Equal(0, rollup.KnownUnsupported);
        Assert.Equal(2, rollup.Unclassified);
    }

    // ---- acquisition metadata is not completeness semantics ------------------

    [Fact]
    public void CapturedVersusReusedIsMergedIntoAvailableAndNeverReexposed()
    {
        var coverage = new[]
        {
            Captured("evidence/messages-a"),
            Reused("evidence/messages-b"),
        };

        var rollup = CanonicalCoverage.From(
            RawGenerationCompleteness.Complete, coverage, diagnostics: []);

        // Both were available and read for the canonical result; how they were reacquired is
        // capture acquisition metadata (docs/RAW_VAULT.md section 4.3, Issue #51).
        Assert.Equal(CanonicalCoverageVerdict.Complete, rollup.Verdict);
        Assert.Equal(2, rollup.Expected);
        Assert.Equal(2, rollup.Available);

        var json = Serialize(rollup);
        Assert.DoesNotContain("captured", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reused", json, StringComparison.Ordinal);
    }

    // ---- determinism and source neutrality -----------------------------------

    [Fact]
    public void TheSameGenerationRollsUpDeterministically()
    {
        const string knownReason = "Known unsupported partition.";
        var manifest = Manifest(RawGenerationCompleteness.Complete,
        [
            Captured("evidence/messages-a"),
            Reused("evidence/sessions"),
            Unsupported("evidence/staging", knownReason),
        ],
        [RawManifestDiagnostic.Info(DiagnosticCodes.PartitionUnsupported, knownReason)]);

        var first = CanonicalCoverage.From(manifest);
        var second = CanonicalCoverage.From(manifest);

        Assert.Equal(first, second);
        Assert.Equal(Serialize(first), Serialize(second));

        // The manifest factory and the parts factory are the same rollup, so a caller holding the
        // just-published capture result computes exactly what the persisted manifest produces.
        Assert.Equal(
            CanonicalCoverage.From(manifest),
            CanonicalCoverage.From(
                manifest.Capture.Completeness, manifest.Coverage, manifest.Diagnostics));
    }

    [Fact]
    public void TheRollupExposesNoSourceSpecificDetail()
    {
        const string knownReason = "Known unsupported partition.";
        var manifest = Manifest(RawGenerationCompleteness.Complete,
        [
            Captured("db_storage/message/message_0.db"),
            Unsupported("db_storage/migrate/unspportmsg.db", knownReason),
        ],
        [RawManifestDiagnostic.Info(DiagnosticCodes.PartitionUnsupported, knownReason)]);

        var json = Serialize(CanonicalCoverage.From(manifest));

        // The canonical contract is counts and a verdict only: no partition ids, no paths, no
        // fingerprints, no checkpoint or manifest internals.
        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(
            new[] { "verdict", "expected", "available", "unavailable", "known_unsupported", "unclassified" },
            names);
        Assert.DoesNotContain("message_0", json, StringComparison.Ordinal);
        Assert.DoesNotContain("unspportmsg", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fingerprint", json, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint", json, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------

    private static RawPartitionCoverage Captured(string id) => new()
    {
        PartitionId = id,
        Status = RawPartitionStatus.Captured,
        SourceFingerprint = "fingerprint-" + id,
        ArtifactSha256 = "sha256-" + id,
    };

    private static RawPartitionCoverage Reused(string id) => new()
    {
        PartitionId = id,
        Status = RawPartitionStatus.Reused,
        SourceFingerprint = "fingerprint-" + id,
        ArtifactSha256 = "sha256-" + id,
    };

    private static RawPartitionCoverage Unavailable(string id, string? diagnostic = null) => new()
    {
        PartitionId = id,
        Status = RawPartitionStatus.Unavailable,
        Diagnostic = diagnostic,
    };

    private static RawPartitionCoverage Unsupported(string id, string? diagnostic = null) => new()
    {
        PartitionId = id,
        Status = RawPartitionStatus.Unsupported,
        Diagnostic = diagnostic,
    };

    private static RawManifest Manifest(
        RawGenerationCompleteness completeness,
        IReadOnlyList<RawPartitionCoverage> coverage,
        IReadOnlyList<RawManifestDiagnostic>? diagnostics = null) => new()
    {
        GenerationId = "gen_0000000000000001",
        AccountId = "a_0000000000000001",
        SourceProfileId = "wxid_synthetic_account",
        Source = new RawManifestSource
        {
            AdapterName = "wechat-windows",
            AdapterVersion = "1.0.0",
            SourceProductName = "WeChat for Windows",
            SourceVersion = "4.1.13.12",
        },
        Capture = new RawManifestCapture
        {
            CaptureTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CaptureAdapterFamily = "wechat-windows",
            CaptureAdapterVersion = "1.0.0",
            Mode = RawCaptureMode.Baseline,
            Completeness = completeness,
        },
        Artifacts = [],
        Coverage = coverage,
        Diagnostics = diagnostics ?? [],
    };

    private static string Serialize(CanonicalCoverage coverage) =>
        JsonSerializer.Serialize(coverage, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
}
