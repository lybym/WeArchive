using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON error document emitted on stdout when a <c>--json</c> invocation fails, so machine
/// callers never have to interpret an empty stdout. The process exit code remains the
/// authoritative outcome class (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
public sealed record CliErrorResultDto
{
    [JsonPropertyName("error")]
    public required CliErrorDto Error { get; init; }
}

public sealed record CliErrorDto
{
    /// <summary>Stable code from <see cref="CommandLine.CliErrorCode"/>.</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>Single-line human-readable cause; the same text is written to stderr.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>
    /// Source-neutral canonical coverage of the failed read. Present only when the failure is an
    /// incomplete-coverage refusal (<c>incomplete_coverage</c>), so a machine caller can
    /// distinguish an incomplete canonical read from other failures without parsing the message
    /// (docs/CLI.md, Issue #51); every other error document is unchanged.
    /// </summary>
    [JsonPropertyName("canonical_coverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CanonicalCoverageDto? CanonicalCoverage { get; init; }
}
