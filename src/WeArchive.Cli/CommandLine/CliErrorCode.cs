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

    /// <summary>Describing a resolved conversation failed (e.g. the source became unavailable mid-operation).</summary>
    public const string ConversationDescribeFailed = "conversation_describe_failed";
}
