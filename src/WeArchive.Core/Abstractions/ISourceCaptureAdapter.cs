using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// A source-specific capture adapter that produces a consistent source snapshot as opaque
/// artifacts written through an <see cref="IRawGenerationSession"/>.
/// <para>
/// The adapter is the only place that knows how to reach a live source, acquire any
/// transient key, and materialize source-faithful database/schema evidence as readable
/// (decrypted) content. It must:
/// </para>
/// <list type="bullet">
/// <item>never modify source files/databases (read-only source boundary, NFR-02);</item>
/// <item>never persist, log or return the upstream database key;</item>
/// <item>preserve source fields the current parser cannot interpret, so unknown evidence
/// remains available in the Raw Vault;</item>
/// <item>report Fatal diagnostics when required evidence is missing or the snapshot is
/// inconsistent, so the generation is never published as complete.</item>
/// </list>
/// <para>
/// Implementations live behind the adapter boundary: WeChat key acquisition, SQLCipher
/// decryption and compatibility logic stay inside <c>src/WeArchive.Infrastructure/WeChat</c>.
/// </para>
/// </summary>
public interface ISourceCaptureAdapter
{
    string CaptureAdapterFamily { get; }

    string CaptureAdapterVersion { get; }

    /// <summary>
    /// Captures a consistent snapshot of the account identified by
    /// <paramref name="sourceProfileId"/>, writing each artifact through
    /// <paramref name="session"/>. Returns the artifact descriptors, diagnostics and a
    /// completeness verdict. A Fatal diagnostic means the caller must discard the generation
    /// rather than publish it.
    /// </summary>
    Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Optional safe incremental path for source families that can prove unchanged partitions.</summary>
public interface IIncrementalSourceCaptureAdapter : ISourceCaptureAdapter
{
    Task<SourceCaptureResult> CaptureIncrementalAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        RawGeneration previous,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of one source-specific capture: the artifacts written, the diagnostics
/// gathered and the completeness verdict. The <see cref="CaptureService"/> builds the manifest
/// from this and decides whether to publish or discard.
/// </summary>
public sealed record SourceCaptureResult
{
    public required IReadOnlyList<RawArtifactDescriptor> Artifacts { get; init; }

    public IReadOnlyList<RawManifestDiagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<RawPartitionCoverage> Coverage { get; init; } = [];
    public RawCaptureMode Mode { get; init; } = RawCaptureMode.Baseline;

    public RawGenerationCompleteness Completeness { get; init; } = RawGenerationCompleteness.Complete;

    /// <summary>
    /// The source product name and version the adapter observed, for the manifest. May be
    /// null when the adapter could not determine them; the caller falls back to the source
    /// descriptor it already has.
    /// </summary>
    public string? SourceProductName { get; init; }

    public string? SourceVersion { get; init; }
}

/// <summary>Shared progress-stage names so the CLI formatter and adapters stay aligned.</summary>
public static class CaptureStages
{
    public const string Acquiring = "acquiring";
    public const string Snapshotting = "snapshotting";
    public const string Finalizing = "finalizing";
}

/// <summary>
/// Thrown when a source capture cannot proceed because the upstream key could not be acquired.
/// This is a Fatal condition: no generation may be published. The exception never carries the
/// key itself, only an engineering reason.
/// </summary>
public sealed class SourceCaptureKeyUnavailableException(string message) : Exception(message);
