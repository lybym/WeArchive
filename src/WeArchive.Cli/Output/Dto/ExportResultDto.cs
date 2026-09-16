using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive export</c>. Exposes the output location, counters and
/// diagnostics of the dataset the <c>IDatasetExporter</c> published, without re-implementing
/// any export packaging rules (docs/PRD.md FR-12, docs/EXPORT_PRD.md, docs/ARCHITECTURE.md
/// section 3.1.1).
/// </summary>
public sealed record ExportResultDto
{
    [JsonPropertyName("succeeded")]
    public required bool Succeeded { get; init; }

    [JsonPropertyName("output_directory")]
    public required string OutputDirectory { get; init; }

    [JsonPropertyName("conversation_ids")]
    public IReadOnlyList<string> ConversationIds { get; init; } = [];

    [JsonPropertyName("record_count")]
    public int RecordCount { get; init; }

    [JsonPropertyName("unknown_count")]
    public int UnknownCount { get; init; }

    [JsonPropertyName("partial_count")]
    public int PartialCount { get; init; }

    [JsonPropertyName("time_range")]
    public ExportTimeRangeDto? TimeRange { get; init; }

    [JsonPropertyName("files")]
    public IReadOnlyList<ExportFileDto> Files { get; init; } = [];

    [JsonPropertyName("conversation_paths")]
    public IReadOnlyList<string> ConversationPaths { get; init; } = [];

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<CliDiagnosticDto> Diagnostics { get; init; } = [];
}

public sealed record ExportTimeRangeDto
{
    [JsonPropertyName("first_message_at")]
    public string? FirstMessageAt { get; init; }

    [JsonPropertyName("last_message_at")]
    public string? LastMessageAt { get; init; }
}

public sealed record ExportFileDto
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("record_count")]
    public int RecordCount { get; init; }
}
