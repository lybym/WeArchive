namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Documented process exit codes. docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1.
/// <para>
/// These are part of the CLI machine contract: callers (scripts, agents) rely on them.
/// </para>
/// </summary>
public static class ExitCode
{
    /// <summary>The requested operation completed under its documented semantics.</summary>
    public const int Success = 0;

    /// <summary>Runtime/operation failure, including a Fatal diagnostic.</summary>
    public const int Failure = 1;

    /// <summary>Command-line usage or configuration validation failure.</summary>
    public const int UsageError = 2;

    /// <summary>User interrupt/cancellation.</summary>
    public const int Cancelled = 130;
}
