using System.Text.Json.Serialization;
using WeArchive.Core.Domain;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// CLI presentation of one <see cref="ImportDiagnostic"/>. Diagnostics never carry chat
/// content: only severity, stable code, a short engineering message, an aggregate count and
/// upstream type codes for provenance. docs/ARCHITECTURE.md section 3.1.1.
/// </summary>
public sealed record CliDiagnosticDto
{
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("source_subtype")]
    public string? SourceSubtype { get; init; }

    /// <summary>
    /// Maps a domain diagnostic to the CLI contract. Severity is lowercased so the wire
    /// representation (<c>fatal</c>/<c>partial</c>/<c>info</c>) is stable regardless of the
    /// enum's declared casing.
    /// </summary>
    public static CliDiagnosticDto From(ImportDiagnostic diagnostic) => new()
    {
        Severity = diagnostic.Severity.ToString().ToLowerInvariant(),
        Code = diagnostic.Code,
        Message = diagnostic.Message,
        Count = diagnostic.Count,
        SourceType = diagnostic.SourceType,
        SourceSubtype = diagnostic.SourceSubtype,
    };
}
