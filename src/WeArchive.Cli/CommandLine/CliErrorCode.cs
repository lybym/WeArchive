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
}
