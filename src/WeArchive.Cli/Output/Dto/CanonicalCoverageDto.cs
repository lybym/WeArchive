using System.Text.Json.Serialization;
using WeArchive.Core.RawVault;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// Source-neutral canonical coverage of one canonical result (docs/CLI.md, Issue #51). The wire
/// names reuse the capture contract's <c>expected</c>/<c>unavailable</c> vocabulary; the
/// capture-side <c>captured</c> versus <c>reused</c> split is acquisition metadata and is
/// deliberately not repeated here — canonical coverage answers whether the supported evidence
/// required by the canonical result was available, and reports known-unsupported versus
/// unknown/unclassified evidence through the source-partition policy's own diagnostic codes.
/// It never carries Raw Vault paths, partition fingerprints, checkpoint JSON or keys.
/// </summary>
public sealed record CanonicalCoverageDto
{
    /// <summary>
    /// Stable wire name of the completeness verdict: <c>complete</c> when the verified
    /// generation proves every required artifact available, <c>incomplete</c> when required
    /// evidence was missing, unreadable or unclassifiable. A successful
    /// <c>succeeded</c>/<c>no_change</c> sync is always <c>complete</c>; <c>incomplete</c>
    /// appears on the <c>incomplete_coverage</c> failure document.
    /// </summary>
    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    /// <summary>Every evidence domain the verified generation accounts for.</summary>
    [JsonPropertyName("expected")]
    public required int Expected { get; init; }

    /// <summary>Expected supported evidence that was available and read for the canonical result.</summary>
    [JsonPropertyName("available")]
    public required int Available { get; init; }

    /// <summary>Expected supported evidence that could not be read or was absent from the source.</summary>
    [JsonPropertyName("unavailable")]
    public required int Unavailable { get; init; }

    /// <summary>Evidence explicitly classified outside the supported contract (<c>partition_unsupported</c>).</summary>
    [JsonPropertyName("known_unsupported")]
    public required int KnownUnsupported { get; init; }

    /// <summary>Evidence without an approved classification (<c>partition_unclassified</c>) or unattributable to a policy diagnostic; conservative.</summary>
    [JsonPropertyName("unclassified")]
    public required int Unclassified { get; init; }

    /// <summary>Maps the application-level rollup onto the wire contract.</summary>
    public static CanonicalCoverageDto From(CanonicalCoverage coverage) => new()
    {
        Verdict = coverage.Verdict == CanonicalCoverageVerdict.Complete ? "complete" : "incomplete",
        Expected = coverage.Expected,
        Available = coverage.Available,
        Unavailable = coverage.Unavailable,
        KnownUnsupported = coverage.KnownUnsupported,
        Unclassified = coverage.Unclassified,
    };
}
