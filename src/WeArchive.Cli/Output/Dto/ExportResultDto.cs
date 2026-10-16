using System.Text.Json.Serialization;
using WeArchive.Core.Export;

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

    /// <summary>
    /// The requested scope this export answered for: which conversation or Collection, the
    /// requested time bounds, and — for a Collection — how much of the declared membership the
    /// canonical archive knows, including the explicitly listed unarchived members
    /// (docs/CLI.md <c>export</c>, docs/EXPORT_PRD.md section 14).
    /// </summary>
    [JsonPropertyName("scope")]
    public ExportScopeDto? Scope { get; init; }

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

/// <summary>
/// The <c>wearchive export</c> scope block, mirroring the manifest's <c>scope</c> field
/// (docs/EXPORT_PRD.md section 14). Collection-only member fields are null (and rendered as JSON
/// null, exactly as <c>message list</c> renders its scope) outside a Collection scope.
/// </summary>
public sealed record ExportScopeDto
{
    /// <summary><c>conversation</c> or <c>collection</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The requested stable conversation ID (conversation scope only).</summary>
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    /// <summary>The requested Collection name (collection scope only).</summary>
    [JsonPropertyName("collection")]
    public string? Collection { get; init; }

    /// <summary>The requested inclusive lower bound, or null when unbounded.</summary>
    [JsonPropertyName("since")]
    public string? Since { get; init; }

    /// <summary>The requested inclusive upper bound, or null when unbounded.</summary>
    [JsonPropertyName("until")]
    public string? Until { get; init; }

    /// <summary>Resolved valid membership entries (collection scope only).</summary>
    [JsonPropertyName("member_conversation_count")]
    public int? MemberConversationCount { get; init; }

    /// <summary>Resolved members the canonical archive knows (collection scope only); the
    /// difference to <see cref="MemberConversationCount"/> is the not-yet-archived remainder,
    /// which contributes no records and is reported in
    /// <see cref="UnarchivedConversationIds"/>.</summary>
    [JsonPropertyName("archived_member_conversation_count")]
    public int? ArchivedMemberConversationCount { get; init; }

    /// <summary>Resolved members the canonical archive does not know, in resolved-membership
    /// order (collection scope only); empty when the archive holds every member, and null outside
    /// a Collection scope.</summary>
    [JsonPropertyName("unarchived_conversation_ids")]
    public IReadOnlyList<string>? UnarchivedConversationIds { get; init; }

    /// <summary>Declared membership entries that are not a valid stable conversation ID
    /// (collection scope only). Reported, never silently dropped.</summary>
    [JsonPropertyName("invalid_member_count")]
    public int? InvalidMemberCount { get; init; }

    /// <summary>Declared membership entries that repeat an earlier valid entry (collection scope
    /// only). Reported, never silently dropped.</summary>
    [JsonPropertyName("duplicate_member_count")]
    public int? DuplicateMemberCount { get; init; }

    public static ExportScopeDto From(ExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return new ExportScopeDto
        {
            Kind = scope.Kind,
            ConversationId = scope.ConversationId,
            Collection = scope.CollectionName,
            Since = scope.Since,
            Until = scope.Until,
            MemberConversationCount = scope.MemberConversationCount,
            ArchivedMemberConversationCount = scope.ArchivedMemberConversationCount,
            // Collection-only member fields stay null outside a Collection scope, exactly as the
            // message list scope renders its collection-only fields (docs/CLI.md).
            UnarchivedConversationIds = scope.UnarchivedConversationIds,
            InvalidMemberCount = scope.InvalidMemberCount,
            DuplicateMemberCount = scope.DuplicateMemberCount,
        };
    }
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
