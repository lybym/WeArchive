namespace WeArchive.Core.Query;

/// <summary>
/// The failure family of a query operation. Transports map the family to their own outcome
/// representation; the vocabulary stays in Core so CLI and a future MCP transport cannot
/// disagree about whether a failure was the caller's input or the archive's state.
/// </summary>
public enum ArchiveQueryFailureKind
{
    /// <summary>The request itself is invalid (malformed filter, bound or cursor).</summary>
    Validation,

    /// <summary>The request was well formed but names something the archive does not hold.</summary>
    NotFound,

    /// <summary>The canonical archive could not be read at all.</summary>
    Unavailable,
}

/// <summary>
/// Stable machine codes of a query failure. They are part of the documented CLI contract
/// (docs/CLI.md) and are never derived from an exception type name.
/// </summary>
public static class ArchiveQueryErrorCodes
{
    /// <summary>A filter, range, bound or identifier in the request is invalid (validation).</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>A pagination cursor is malformed, unsupported or belongs to another query (validation).</summary>
    public const string CursorInvalid = "cursor_invalid";

    /// <summary>The requested stable conversation ID is not in the archive.</summary>
    public const string ConversationNotFound = "conversation_not_found";

    /// <summary>The requested stable message ID is not in the archive.</summary>
    public const string MessageNotFound = "message_not_found";

    /// <summary>The canonical archive could not be opened or queried.</summary>
    public const string ArchiveUnavailable = "archive_unavailable";

    /// <summary>
    /// A search query is empty or contains a term without any letter, digit or CJK ideograph
    /// (docs/SEARCH_SPEC.md section 5.3, M3b index slice).
    /// </summary>
    public const string QueryInvalid = "query_invalid";

    /// <summary>
    /// The derived FTS index is missing, inconsistent with the canonical archive, or failed
    /// verification; search fails closed instead of returning partial results
    /// (docs/SEARCH_SPEC.md section 10.4, M3b index slice).
    /// </summary>
    public const string SearchIndexUnavailable = "search_index_unavailable";

    /// <summary>The requested Collection name is not defined by the authoritative catalog.</summary>
    public const string CollectionNotFound = "collection_not_found";

    /// <summary>The authoritative Collection configuration exists but is not valid.</summary>
    public const string CollectionConfigInvalid = "collection_config_invalid";
}

/// <summary>
/// A query operation failed in a documented, deterministic way. Query is read-only, so no failure
/// carries recovery semantics: nothing canonical, captured or checkpointed was modified.
/// </summary>
public sealed class ArchiveQueryException : Exception
{
    public ArchiveQueryException(ArchiveQueryFailureKind kind, string code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public ArchiveQueryException(ArchiveQueryFailureKind kind, string code, string message, Exception inner)
        : base(message, inner)
    {
        Kind = kind;
        Code = code;
    }

    public ArchiveQueryFailureKind Kind { get; }

    /// <summary>Stable machine code, one of <see cref="ArchiveQueryErrorCodes"/>.</summary>
    public string Code { get; }

    public static ArchiveQueryException Invalid(string message) =>
        new(ArchiveQueryFailureKind.Validation, ArchiveQueryErrorCodes.InvalidRequest, message);

    public static ArchiveQueryException InvalidCursor(string message) =>
        new(ArchiveQueryFailureKind.Validation, ArchiveQueryErrorCodes.CursorInvalid, message);

    public static ArchiveQueryException ConversationNotFound(string conversationId) =>
        new(
            ArchiveQueryFailureKind.NotFound,
            ArchiveQueryErrorCodes.ConversationNotFound,
            $"conversation '{conversationId}' was not found in the archive.");

    public static ArchiveQueryException MessageNotFound(string messageId) =>
        new(
            ArchiveQueryFailureKind.NotFound,
            ArchiveQueryErrorCodes.MessageNotFound,
            $"message '{messageId}' was not found in the archive.");

    /// <summary>
    /// The canonical archive could not be read. A missing archive file is not this failure: the
    /// store creates and migrates it, so it reads as an empty archive, exactly as
    /// <c>doctor</c> reports it.
    /// </summary>
    public static ArchiveQueryException Unavailable(Exception inner) =>
        new(
            ArchiveQueryFailureKind.Unavailable,
            ArchiveQueryErrorCodes.ArchiveUnavailable,
            // The exception type is included so an unexpected defect stays diagnosable instead of
            // being indistinguishable from an unreadable archive.
            $"the canonical archive could not be queried ({inner.GetType().Name}): {inner.Message}",
            inner);

    /// <summary>
    /// A search query failed deterministic usage validation before any index access. The message
    /// never echoes the query content (docs/SEARCH_SPEC.md section 5.3).
    /// </summary>
    public static ArchiveQueryException QueryInvalid(string message) =>
        new(ArchiveQueryFailureKind.Validation, ArchiveQueryErrorCodes.QueryInvalid, message);

    /// <summary>
    /// The derived search index could not serve this query. Search is read-only (R0): this failure
    /// carries no repair semantics; recovery is the explicit index rebuild of
    /// docs/SEARCH_SPEC.md section 10.3. The reason never contains message or index content.
    /// </summary>
    public static ArchiveQueryException IndexUnavailable(string reason) =>
        new(ArchiveQueryFailureKind.Unavailable, ArchiveQueryErrorCodes.SearchIndexUnavailable, reason);

    /// <summary>
    /// A requested Collection name is not defined. The message names the collection, which is the
    /// caller's own input, not archive content.
    /// </summary>
    public static ArchiveQueryException CollectionNotFound(string name) =>
        new(
            ArchiveQueryFailureKind.NotFound,
            ArchiveQueryErrorCodes.CollectionNotFound,
            $"collection '{name}' is not defined in the collection configuration.");

    /// <summary>The authoritative Collection configuration exists but is not valid.</summary>
    public static ArchiveQueryException CollectionConfigInvalid(Exception inner) =>
        new(
            ArchiveQueryFailureKind.Validation,
            ArchiveQueryErrorCodes.CollectionConfigInvalid,
            $"the collection configuration is invalid: {inner.Message}",
            inner);
}