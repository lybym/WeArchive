using WeArchive.Cli.CommandLine;

namespace WeArchive.Cli.Commands;

/// <summary>
/// Bridges an <c>IProgress&lt;T&gt;</c> callback from an application service to the CLI's
/// stderr progress stream. It is synchronous (no <see cref="Progress{T}"/> async post) so
/// progress lines are emitted in operation order.
/// <para>
/// Suppression is not re-implemented here: <see cref="CliContext.ReportProgress"/> already
/// keeps progress off stderr under <c>--quiet</c> and <c>--json</c>, so the machine stream on
/// stdout is never interleaved with progress (docs/PRD.md FR-22, docs/ARCHITECTURE.md
/// section 3.1.1).
/// </para>
/// </summary>
internal sealed class CliProgress<T> : IProgress<T>
{
    private readonly CliContext _context;
    private readonly Func<T, string> _format;

    internal CliProgress(CliContext context, Func<T, string> format)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    public void Report(T value) => _context.ReportProgress(_format(value));
}
