using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output.Dto;

namespace WeArchive.Cli.Output;

/// <summary>
/// Single implementation of the failure half of the CLI process contract: the human
/// diagnostic goes to stderr and, when <c>--json</c> was requested, stdout receives
/// exactly one JSON error document — never prose and never an empty stream.
/// docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1.
/// </summary>
public static class CliErrors
{
    /// <summary>
    /// Writes <paramref name="message"/> to <paramref name="stderr"/> and, in JSON mode,
    /// one <see cref="CliErrorResultDto"/> document to <paramref name="stdout"/>.
    /// </summary>
    public static void Write(
        TextWriter stdout,
        TextWriter stderr,
        GlobalOptions options,
        string code,
        string message,
        CanonicalCoverageDto? canonicalCoverage = null)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);

        // Humans always get the diagnostic, including under --quiet: a failure is
        // essential output, not suppressible progress.
        stderr.WriteLine($"error: {message}");

        if (!options.Json)
            return;

        stdout.WriteLine(CliJson.Serialize(new CliErrorResultDto
        {
            Error = new CliErrorDto
            {
                Code = code,
                Message = message,
                CanonicalCoverage = canonicalCoverage,
            },
        }));
    }
}
