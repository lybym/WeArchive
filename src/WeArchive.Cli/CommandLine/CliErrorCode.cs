namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Stable <c>error.code</c> values of the CLI JSON error document. The values mirror the
/// documented exit-code families so a caller can branch on either representation
/// (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
public static class CliErrorCode
{
    /// <summary>Usage/configuration validation failure (exit 2).</summary>
    public const string UsageError = "usage_error";

    /// <summary>Runtime/operation failure (exit 1).</summary>
    public const string Failure = "failure";

    /// <summary>User interrupt/cancellation (exit 130).</summary>
    public const string Cancelled = "cancelled";

    // ---- Discovery family (Issue #7 / M0.5) ----
    // These refine the generic failure code for the discovery commands so a machine caller can
    // branch on the cause. They all map to exit 1 (runtime/operation failure); a usage-shaped
    // problem remains `usage_error` (exit 2) and cancellation remains `cancelled` (exit 130).

    /// <summary>The source is not available/running (account or conversation listing failed to reach it).</summary>
    public const string SourceUnavailable = "source_unavailable";

    /// <summary>The source is available but exposes no profiles, so a conversation command cannot resolve an account.</summary>
    public const string NoAccounts = "no_accounts";

    /// <summary>The <c>--account</c> selector did not match any available profile.</summary>
    public const string AccountNotFound = "account_not_found";

    /// <summary>Enumerating conversations for a profile failed (after the source/account was resolved).</summary>
    public const string ConversationListFailed = "conversation_list_failed";

    /// <summary>A conversation identifier did not resolve to any known conversation.</summary>
    public const string ConversationNotFound = "conversation_not_found";

    /// <summary>
    /// An upstream source conversation id matched more than one archived conversation, so the
    /// archive-backed selector refused to choose one arbitrarily. Deterministic runtime failure
    /// (exit 1); the caller must use the canonical stable id (Issue #66).
    /// </summary>
    public const string ConversationAmbiguous = "conversation_ambiguous";

    /// <summary>Describing a resolved conversation failed (e.g. the source became unavailable mid-operation).</summary>
    public const string ConversationDescribeFailed = "conversation_describe_failed";

    // ---- Collection scope (Issue #26 / M4 foundation) ----
    // `collection_config_invalid` is a configuration validation failure (exit 2), matching the
    // documented exit-code family; the other two are runtime/operation failures (exit 1).

    /// <summary>A requested Collection name is not defined by the authoritative configuration.</summary>
    public const string CollectionNotFound = "collection_not_found";

    /// <summary>The user-maintained Collection configuration exists but is invalid (exit 2).</summary>
    public const string CollectionConfigInvalid = "collection_config_invalid";

    /// <summary>Capturing live-source evidence for a Collection did not publish a usable generation.</summary>
    public const string CaptureFailed = "capture_failed";

    // ---- Query family (Issue #27 / M3a) ----
    // `cursor_invalid` is caller-input validation (exit 2), matching the documented exit-code
    // family; the other two are runtime/operation failures (exit 1).

    /// <summary>A stable message ID did not resolve to any archived message.</summary>
    public const string MessageNotFound = "message_not_found";

    /// <summary>A pagination cursor is malformed, unsupported or belongs to a different query (exit 2).</summary>
    public const string CursorInvalid = "cursor_invalid";

    /// <summary>
    /// The canonical archive could not be read. A missing archive file is not this failure: the
    /// store creates and migrates it, so it reads as an empty archive, exactly as <c>doctor</c>
    /// reports it.
    /// </summary>
    public const string ArchiveUnavailable = "archive_unavailable";

    // ---- Canonical coverage (Issue #51 / M1b) ----

    /// <summary>
    /// The published Raw Vault generation's evidence coverage is not complete, so the R2 ingest
    /// refused the canonical read instead of publishing a result that could be mistaken for a
    /// complete one (docs/PRD.md FR-20, docs/RAW_VAULT.md section 7). Runtime failure (exit 1);
    /// the JSON error document carries the source-neutral <c>canonical_coverage</c> rollup.
    /// </summary>
    public const string IncompleteCoverage = "incomplete_coverage";
}
