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
}
