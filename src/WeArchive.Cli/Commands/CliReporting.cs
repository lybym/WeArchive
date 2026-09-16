using WeArchive.Cli.CommandLine;

namespace WeArchive.Cli.Commands;

/// <summary>
/// Shared presentation-layer progress reporting: the single seam commands use for their direct
/// progress lines instead of writing to <see cref="CliContext.Stderr"/> themselves.
/// <para>
/// This intentionally adds no suppression rule of its own. The rule lives once, in
/// <see cref="CliContext.ReportProgress"/>, which suppresses progress when <c>--quiet</c> or
/// <c>--json</c> is set — so <c>--json</c> mode keeps stderr free of progress noise and stdout
/// stays the single machine document (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1).
/// Re-deriving the <c>--json</c> rule here would put the same contract in a second place that
/// could drift from the foundation.
/// </para>
/// </summary>
internal static class CliReporting
{
    internal static void Progress(CliContext context, string message) =>
        context.ReportProgress(message);
}
