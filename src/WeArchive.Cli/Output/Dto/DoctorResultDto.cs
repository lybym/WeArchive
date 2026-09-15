using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive doctor</c>. Reports source and archive readiness without
/// changing source data (docs/PRD.md FR-02).
/// </summary>
public sealed record DoctorResultDto
{
    [JsonPropertyName("ready")]
    public required bool Ready { get; init; }

    [JsonPropertyName("source")]
    public required SourceCheckDto Source { get; init; }

    [JsonPropertyName("archive")]
    public required ArchiveCheckDto Archive { get; init; }
}

public sealed record SourceCheckDto
{
    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    [JsonPropertyName("adapter")]
    public required string Adapter { get; init; }

    [JsonPropertyName("adapter_version")]
    public string? AdapterVersion { get; init; }

    [JsonPropertyName("source_version")]
    public string? SourceVersion { get; init; }

    [JsonPropertyName("source_product")]
    public string? SourceProduct { get; init; }

    [JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; init; }
}

public sealed record ArchiveCheckDto
{
    [JsonPropertyName("available")]
    public required bool Available { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("account_count")]
    public int AccountCount { get; init; }

    [JsonPropertyName("conversation_count")]
    public int ConversationCount { get; init; }

    [JsonPropertyName("participant_count")]
    public int ParticipantCount { get; init; }

    [JsonPropertyName("message_count")]
    public int MessageCount { get; init; }

    [JsonPropertyName("unavailable_reason")]
    public string? UnavailableReason { get; init; }
}
