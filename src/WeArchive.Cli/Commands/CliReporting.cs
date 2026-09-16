using WeArchive.Cli.CommandLine;

namespace WeArchive.Cli.Commands;

/// <summary>
/// Shared presentation-layer progress reporting. The foundation
/// <see cref="CliContext.ReportProgress"/> suppresses only <c>--quiet</c>; commands route
/// their direct progress lines through this helper so <c>--json</c> mode keeps stderr free
/// of progress noise as well, leaving stdout as the single machine document
/// (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
internal static class CliReporting
{
    internal static void Progress(CliContext context, string message)
    {
        if (!context.Options.Json)
        {
            context.ReportProgress(message);
        }
    }
}
