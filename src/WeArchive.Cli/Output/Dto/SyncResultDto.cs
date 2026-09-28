using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive sync --conversation</c>. It mirrors the application-level
/// <c>ConversationSyncResult</c> without re-deriving any archive semantics: the documented
/// <c>succeeded</c>/<c>no_change</c> outcome, the generation the conversation was ingested from and
/// the capture mode, so a caller can see that a repeat sync reused verified evidence instead of
/// republishing it (docs/PRD.md G4/FR-14/FR-28, docs/CLI.md, Issue #49).
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

    /// <summary>
    /// Stable wire name of the outcome: <c>succeeded</c> (evidence changed and this
    /// conversation's canonical publication committed) or <c>no_change</c> (the conversation was
    /// verified and nothing changed, so nothing was republished and its checkpoint kept its value).
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Conversations published by this run (0 or 1).</summary>
    [JsonPropertyName("conversations_ingested")]
    public int ConversationsIngested { get; init; }

    /// <summary>The Raw Vault generation this run's evidence came from.</summary>
    [JsonPropertyName("generation_id")]
    public required string GenerationId { get; init; }

    /// <summary><c>baseline</c> when the whole supported source was read, <c>incremental</c> when verified evidence was reused.</summary>
    [JsonPropertyName("capture_mode")]
    public required string CaptureMode { get; init; }

    /// <summary>The generation this one extends; null for the first published generation.</summary>
    [JsonPropertyName("previous_generation_id")]
    public string? PreviousGenerationId { get; init; }
}
