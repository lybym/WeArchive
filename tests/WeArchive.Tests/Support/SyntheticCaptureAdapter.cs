using System.Text;
using WeArchive.Core.Abstractions;
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

    /// <summary>Expected partitions this adapter version cannot represent at all.</summary>
    public HashSet<string> Unsupported { get; } = new(StringComparer.Ordinal);

    /// <summary>Expected partitions the adapter could not read.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, the adapter fails instead of capturing (I/O or cancellation).</summary>
    public Exception? Failure { get; set; }

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
        var complete = true;

        foreach (var partitionId in Partitions.Keys.OrderBy(id => id, StringComparer.Ordinal))
        {
            var fingerprint = Partitions[partitionId];

            if (Unsupported.Contains(partitionId))
            {
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = partitionId,
                    Status = RawPartitionStatus.Unsupported,
                    Diagnostic = $"Partition '{partitionId}' is not supported by adapter version {CaptureAdapterVersion}.",
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

            using var content = new MemoryStream(Encoding.UTF8.GetBytes($"{partitionId}:{fingerprint}"));
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

        return new SourceCaptureResult
        {
            Artifacts = artifacts,
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