using System.Text.Json.Serialization;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Export;

/// <summary>Phase 1 export request. docs/EXPORT_PRD.md sections 7 and 9.</summary>
public sealed record ExportRequest
{
    /// <summary>Absolute directory that will receive the export package.</summary>
    public required string OutputDirectory { get; init; }

    public required string AccountId { get; init; }

    /// <summary>Stable conversation IDs to export. MVP exports exactly one at a time.</summary>
    public required IReadOnlyList<string> ConversationIds { get; init; }

    /// <summary>
    /// Export creation time. Injected so that deterministic tests can pin it;
    /// it is the only non-reproducible field in the package.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Optional requested lower time bound. Inclusive, compared at the archive's one-second
    /// resolution: a record whose canonical instant equals the bound is exported, and a
    /// sub-second bound component is truncated before comparison (docs/CLI.md time grammar).
    /// Null keeps the side unbounded.
    /// </summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Optional requested upper time bound; same inclusive one-second semantics as
    /// <see cref="Since"/>. Null keeps the side unbounded.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>
    /// The requested export scope, resolved by the caller. It is written into
    /// <c>manifest.json</c> verbatim so a consumer can tell what the package was selected for —
    /// including unarchived Collection members that therefore contribute no records
    /// (docs/EXPORT_PRD.md sections 7, 8 and 14).
    /// </summary>
    public ExportScope? Scope { get; init; }

    /// <summary>
    /// Diagnostics accumulated while producing the archive rows for this export. They are
    /// rolled up into <c>manifest.json</c> so a consumer can judge completeness without
    /// reading any timeline file.
    /// </summary>
    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ExportProgress
{
    public required string Stage { get; init; }

    public int Processed { get; init; }

    public int Total { get; init; }

    public string? ConversationId { get; init; }
}

/// <summary>One file written by an export, with its path relative to the package root.</summary>
public sealed record ExportedFile
{
    public required string RelativePath { get; init; }

    public required string Kind { get; init; }

    public int RecordCount { get; init; }

    public long SizeBytes { get; init; }
}

public sealed record ExportResult
{
    public required bool Succeeded { get; init; }

    public required string OutputDirectory { get; init; }

    public string? FailureReason { get; init; }

    public IReadOnlyList<ExportedFile> Files { get; init; } = [];

    public IReadOnlyList<string> ConversationIds { get; init; } = [];

    public int RecordCount { get; init; }

    public int UnknownCount { get; init; }

    public int PartialCount { get; init; }

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Root-relative paths of the per-conversation folders, for UI display.</summary>
    public IReadOnlyList<string> ConversationPaths { get; init; } = [];

    /// <summary>
    /// The requested scope this export answered for, as written into the manifest. Set by the
    /// export workflow; the exporter itself stays scope-agnostic (docs/EXPORT_PRD.md section 14).
    /// </summary>
    public ExportScope? Scope { get; init; }
}

/// <summary>
/// The requested export scope of one export invocation: which conversation or Collection it was
/// selected for, the requested time bounds, and — for a Collection — how much of the declared
/// membership the canonical archive knows. Unarchived members contribute no records and are
/// reported explicitly instead of being silently narrowed away, so a package never reads as more
/// complete than its basis (docs/PRD.md FR-23, docs/EXPORT_PRD.md sections 7, 8 and 14).
/// <para>
/// Serialized into <c>manifest.json</c> and mirrored on <see cref="ExportResult"/>; string bounds
/// use the same ISO-8601 render form as every other manifest timestamp.
/// </para>
/// </summary>
public sealed record ExportScope
{
    /// <summary><c>conversation</c> or <c>collection</c>.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>The requested stable conversation ID (conversation scope only).</summary>
    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    /// <summary>The requested Collection name (collection scope only).</summary>
    [JsonPropertyName("collection")]
    public string? CollectionName { get; init; }

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
    /// which contributes no records and is never silently narrowed away.</summary>
    [JsonPropertyName("archived_member_conversation_count")]
    public int? ArchivedMemberConversationCount { get; init; }

    /// <summary>Resolved members the canonical archive does not know, in resolved-membership
    /// order (collection scope only). Explicitly listed so missing members stay visible; empty
    /// when the archive holds every member.</summary>
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
}

/// <summary>
/// <c>manifest.json</c>. docs/EXPORT_PRD.md section 14; a consumer reads this before
/// deciding which timeline files to load.
/// </summary>
public sealed record ExportManifest
{
    [JsonPropertyName("export_schema_version")]
    public required string ExportSchemaVersion { get; init; }

    [JsonPropertyName("message_schema_version")]
    public required string MessageSchemaVersion { get; init; }

    [JsonPropertyName("exporter_version")]
    public required string ExporterVersion { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("source_account_id")]
    public required string SourceAccountId { get; init; }

    [JsonPropertyName("source_adapter")]
    public string? SourceAdapter { get; init; }

    [JsonPropertyName("source_version")]
    public string? SourceVersion { get; init; }

    [JsonPropertyName("conversation_ids")]
    public required IReadOnlyList<string> ConversationIds { get; init; }

    /// <summary>
    /// The requested scope of this export: which conversation or Collection it was selected for,
    /// the requested time bounds, and — for a Collection — the declared-membership coverage the
    /// canonical archive actually knows, including the explicitly listed unarchived members.
    /// docs/EXPORT_PRD.md section 14.
    /// </summary>
    [JsonPropertyName("scope")]
    public ExportScope? Scope { get; init; }

    [JsonPropertyName("time_range")]
    public ManifestTimeRange? TimeRange { get; init; }

    [JsonPropertyName("record_count")]
    public int RecordCount { get; init; }

    [JsonPropertyName("unknown_count")]
    public int UnknownCount { get; init; }

    [JsonPropertyName("partial_count")]
    public int PartialCount { get; init; }

    [JsonPropertyName("unsupported_count")]
    public int UnsupportedCount { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<ManifestFile> Files { get; init; }

    [JsonPropertyName("conversations")]
    public required IReadOnlyList<ManifestConversation> Conversations { get; init; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<ManifestDiagnostic> Diagnostics { get; init; } = [];
}

public sealed record ManifestTimeRange
{
    [JsonPropertyName("first_message_at")]
    public string? FirstMessageAt { get; init; }

    [JsonPropertyName("last_message_at")]
    public string? LastMessageAt { get; init; }
}

public sealed record ManifestFile
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("conversation_id")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("month")]
    public int? Month { get; init; }

    [JsonPropertyName("record_count")]
    public int RecordCount { get; init; }
}

public sealed record ManifestConversation
{
    [JsonPropertyName("conversation_id")]
    public required string ConversationId { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("current_name")]
    public string? CurrentName { get; init; }

    [JsonPropertyName("record_count")]
    public int RecordCount { get; init; }

    [JsonPropertyName("first_message_at")]
    public string? FirstMessageAt { get; init; }

    [JsonPropertyName("last_message_at")]
    public string? LastMessageAt { get; init; }
}

public sealed record ManifestDiagnostic
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
}
