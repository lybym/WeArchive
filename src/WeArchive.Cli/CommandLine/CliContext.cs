using WeArchive.Cli.Output;

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
    /// Writes a progress/diagnostic line to stderr unless <c>--quiet</c> or <c>--json</c>
    /// suppressed it. <c>--json</c> suppresses progress so machine-readable stdout is never
    /// interleaved with human progress on the same terminal, matching the documented CLI
    /// contract (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1). Errors are never
    /// suppressed; see <see cref="WriteError"/>.
    /// </summary>
    public void ReportProgress(string message)
    {
        if (!Options.Quiet && !Options.Json)
            Stderr.WriteLine(message);
    }

    /// <summary>
    /// Reports a failure: a human diagnostic on stderr plus, in <c>--json</c> mode, exactly
    /// one JSON error document on stdout. Commands and the router must use this instead of
    /// writing to <see cref="Stderr"/> directly, so the machine contract cannot be bypassed.
    /// docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1.
    /// </summary>
    public void WriteError(string code, string message) =>
        CliErrors.Write(Stdout, Stderr, Options, code, message);

    /// <summary>
    /// Reports a failure that also carries structured coverage detail — the incomplete-coverage
    /// refusal (<c>incomplete_coverage</c>): the same human diagnostic on stderr, and in
    /// <c>--json</c> mode the source-neutral <c>canonical_coverage</c> rollup inside the one
    /// stdout error document. docs/CLI.md, docs/PRD.md FR-20, Issue #51.
    /// </summary>
    public void WriteError(string code, string message, Output.Dto.CanonicalCoverageDto canonicalCoverage) =>
        CliErrors.Write(Stdout, Stderr, Options, code, message, canonicalCoverage);

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
