using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive sync</c>. Reports the import-run audit counters and
/// diagnostics the <c>ImportService</c> produced, without re-deriving any archive
/// semantics (docs/PRD.md FR-04/FR-09, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
public sealed record SyncResultDto
{
    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("account_id")]
    public required string AccountId { get; init; }

    [JsonPropertyName("source_profile_id")]
    public required string SourceProfileId { get; init; }

    [JsonPropertyName("source_conversation_id")]
    public required string SourceConversationId { get; init; }

    [JsonPropertyName("records_scanned")]
    public int RecordsScanned { get; init; }

    [JsonPropertyName("counters")]
    public required SyncCountersDto Counters { get; init; }

    [JsonPropertyName("first_message_at")]
    public string? FirstMessageAt { get; init; }

    [JsonPropertyName("last_message_at")]
    public string? LastMessageAt { get; init; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<CliDiagnosticDto> Diagnostics { get; init; } = [];
}

/// <summary>
/// Counters a completed sync published to the archive. A rolled-back (Fatal) run produces no
/// result document, so these always describe committed state.
/// </summary>
public sealed record SyncCountersDto
{
    [JsonPropertyName("inserted")]
    public int Inserted { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("unchanged")]
    public int Unchanged { get; init; }

    [JsonPropertyName("unknown")]
    public int Unknown { get; init; }

    [JsonPropertyName("partial")]
    public int Partial { get; init; }
}
