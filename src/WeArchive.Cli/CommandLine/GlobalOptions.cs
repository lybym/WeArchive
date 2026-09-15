namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Global options recognised before and after the command name.
/// docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1.
/// </summary>
public sealed record GlobalOptions
{
    /// <summary>
    /// <c>--json</c>: emit exactly one JSON document on stdout with no ANSI or prose.
    /// </summary>
    public bool Json { get; init; }

    /// <summary>
    /// <c>--quiet</c>: suppress non-essential progress/diagnostics on stderr.
    /// </summary>
    public bool Quiet { get; init; }

    /// <summary>
    /// <c>--no-input</c>: never prompt; fail when required input is missing.
    /// </summary>
    public bool NoInput { get; init; }

    /// <summary><c>--version</c>: print product version and exit.</summary>
    public bool ShowVersion { get; init; }

    /// <summary><c>--help</c> / <c>-h</c>: print help and exit.</summary>
    public bool ShowHelp { get; init; }

    /// <summary>Convenience factory used by tests and default contexts.</summary>
    public static GlobalOptions Default { get; } = new();
}
