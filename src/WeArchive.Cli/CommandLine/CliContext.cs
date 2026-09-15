namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Execution context passed to every command. Holds the stdout/stderr writers and the
/// parsed global options so commands remain pure adapters that do not touch
/// <see cref="Console"/> directly (which is not testable).
/// </summary>
public sealed class CliContext
{
    public CliContext(TextWriter stdout, TextWriter stderr, GlobalOptions options)
    {
        Stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        Stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Final result stream. In <c>--json</c> mode this receives exactly one JSON document
    /// and nothing else. docs/ARCHITECTURE.md section 3.1.1.
    /// </summary>
    public TextWriter Stdout { get; }

    /// <summary>Progress, warnings and human diagnostics stream.</summary>
    public TextWriter Stderr { get; }

    /// <summary>Parsed global options.</summary>
    public GlobalOptions Options { get; }

    /// <summary>
    /// Writes a progress/diagnostic line to stderr unless <c>--quiet</c> suppressed it.
    /// Use this for all non-essential progress so <c>--quiet</c> behaves consistently.
    /// </summary>
    public void ReportProgress(string message)
    {
        if (!Options.Quiet)
            Stderr.WriteLine(message);
    }

    /// <summary>
    /// Throws <see cref="CliUsageException"/> when <c>--no-input</c> is set, because the
    /// command would need to prompt and the contract forbids prompting in that mode.
    /// docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1.
    /// </summary>
    public void FailIfNoInput(string message)
    {
        if (Options.NoInput)
            throw new CliUsageException(message);
    }
}
