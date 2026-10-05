using System.Text;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Tests.Support;

/// <summary>
/// Deterministic synthetic capture adapter for incremental-capture semantics (Issue #25).
/// <para>
/// Partitions are declared as <c>id -&gt; fingerprint</c> and each is materialized as a tiny
/// artifact whose content is the partition id plus the fingerprint, so an unchanged partition
/// can be reused without touching a live source. It is the fixture equivalent of the WeChat
/// adapter's fingerprint-and-reuse path and keeps the capture/checkpoint tests independent of
/// WeChat and of a running client.
/// </para>
/// </summary>
internal sealed class SyntheticCaptureAdapter : IIncrementalSourceCaptureAdapter
{
    private const string SourceDatabaseRole = "source-database";

    public string CaptureAdapterFamily => "synthetic";

    /// <summary>Mutable so tests can reproduce an adapter version change.</summary>
    public string CaptureAdapterVersion { get; set; } = "1";

    /// <summary>Live source partitions: opaque partition id -> current source fingerprint.</summary>
    public Dictionary<string, string> Partitions { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, byte[]> ContentOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Known-unsupported partitions: recorded as <c>unsupported</c> coverage without downgrading
    /// completeness, mirroring the documented source-partition policy (Issue #37).
    /// </summary>
    public HashSet<string> Unsupported { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Unclassified partitions: recorded as <c>unsupported</c> coverage with a partial diagnostic
    /// and a <c>partial</c> verdict, mirroring the documented Unknown class (Issue #37).
    /// </summary>
    public HashSet<string> Unclassified { get; } = new(StringComparer.Ordinal);

    /// <summary>Expected partitions the adapter could not read.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, the adapter fails instead of capturing (I/O or cancellation).</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// When set, the adapter reports <c>complete</c> even though its own coverage/diagnostics
    /// contradict it, so the Core completeness guard can be exercised (docs/PRD.md FR-20).
    /// </summary>
    public bool ReportCompleteDespiteCoverageGaps { get; set; }

    public Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken) =>
        CaptureCoreAsync(session, null, cancellationToken);

    public Task<SourceCaptureResult> CaptureIncrementalAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        RawGeneration previous,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken) =>
        CaptureCoreAsync(session, previous, cancellationToken);

    private async Task<SourceCaptureResult> CaptureCoreAsync(
        IRawGenerationSession session,
        RawGeneration? previous,
        CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var prior = previous?.Manifest.Coverage
            .Where(c => c.Status is RawPartitionStatus.Captured or RawPartitionStatus.Reused)
            .ToDictionary(c => c.PartitionId, StringComparer.Ordinal);

        var artifacts = new List<RawArtifactDescriptor>();
        var coverage = new List<RawPartitionCoverage>();
        var diagnostics = new List<RawManifestDiagnostic>();
        var complete = true;

        foreach (var partitionId in Partitions.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            var fingerprint = Partitions[partitionId];

            if (Unsupported.Contains(partitionId))
            {
                // Known unsupported: visible in coverage, excluded from the checkpoint, and not by
                // itself a reason to downgrade the generation.
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partitionId,
                    Status = RawPartitionStatus.Unsupported,
                    Diagnostic = $"Partition '{partitionId}' is known-unsupported by adapter version {CaptureAdapterVersion}.",
                });
                continue;
            }

            if (Unclassified.Contains(partitionId))
            {
                var message =
                    $"Partition '{partitionId}' is unclassified by adapter version {CaptureAdapterVersion}.";
                diagnostics.Add(RawManifestDiagnostic.Partial(DiagnosticCodes.PartitionUnclassified, message));
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partitionId,
                    Status = RawPartitionStatus.Unsupported,
                    Diagnostic = message,
                });
                complete = false;
                continue;
            }

            if (Unreadable.Contains(partitionId))
            {
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partitionId,
                    Status = RawPartitionStatus.Unavailable,
                    Diagnostic = $"Partition '{partitionId}' could not be read.",
                });
                complete = false;
                continue;
            }

            var reusable = FindReusableArtifact(previous, prior, partitionId, fingerprint);
            if (reusable is not null)
            {
                var reused = await session
                    .ReuseArtifactAsync(previous!, reusable, cancellationToken)
                    .ConfigureAwait(false);
                artifacts.Add(reused);
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partitionId,
                    Status = RawPartitionStatus.Reused,
                    SourceFingerprint = fingerprint,
                    ArtifactSha256 = reused.Sha256,
                });
                continue;
            }

            using var content = new MemoryStream(ContentOverrides.TryGetValue(partitionId, out var bytes)
                ? bytes : Encoding.UTF8.GetBytes($"{partitionId}:{fingerprint}"));
            var descriptor = await session
                .WriteArtifactAsync(
                    SourceDatabaseRole,
                    partitionId + ".db",
                    content,
                    sourceFormat: "sqlite",
                    isDecrypted: true,
                    metadata: null,
                    cancellationToken)
                .ConfigureAwait(false);
            artifacts.Add(descriptor);
            coverage.Add(new RawPartitionCoverage
            {
                PartitionId = partitionId,
                Status = RawPartitionStatus.Captured,
                SourceFingerprint = fingerprint,
                ArtifactSha256 = descriptor.Sha256,
            });
        }

        // A partition the previous generation covered but the live source no longer exposes is
        // reported as unavailable. It never removes the earlier generation or its evidence.
        if (prior is not null)
        {
            foreach (var partitionId in prior.Keys
                .Except(Partitions.Keys, StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal))
            {
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partitionId,
                    Status = RawPartitionStatus.Unavailable,
                    Diagnostic = $"Partition '{partitionId}' is absent from the live source.",
                });
                complete = false;
            }
        }

        var completeness = artifacts.Count == 0
            ? RawGenerationCompleteness.Incomplete
            : complete ? RawGenerationCompleteness.Complete : RawGenerationCompleteness.Partial;

        if (ReportCompleteDespiteCoverageGaps && artifacts.Count > 0)
        {
            completeness = RawGenerationCompleteness.Complete;
        }

        return new SourceCaptureResult
        {
            Artifacts = artifacts,
            Diagnostics = diagnostics,
            Coverage = coverage,
            Mode = previous is null ? RawCaptureMode.Baseline : RawCaptureMode.Incremental,
            Completeness = completeness,
        };
    }

    private static RawArtifactDescriptor? FindReusableArtifact(
        RawGeneration? previous,
        Dictionary<string, RawPartitionCoverage>? prior,
        string partitionId,
        string fingerprint)
    {
        if (previous is null ||
            prior is null ||
            !prior.TryGetValue(partitionId, out var entry) ||
            !string.Equals(entry.SourceFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        var name = partitionId + ".db";
        return previous.Manifest.Artifacts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
    }
}
