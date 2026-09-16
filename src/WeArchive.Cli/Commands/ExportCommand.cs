using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive export --conversation &lt;id-or-alias&gt;</c>: publishes one conversation's
/// JSONL dataset (docs/PRD.md FR-12, docs/EXPORT_PRD.md, ROADMAP M0.5).
/// <para>
/// This is a thin transport adapter. It resolves the requested conversation through the source
/// catalog (reusing the same stable upstream identifier <see cref="ImportService"/> consumes)
/// and delegates the whole source -&gt; archive -&gt; dataset operation to
/// <see cref="ArchiveWorkflow"/>, which re-imports idempotently and then exports from the
/// SQLite archive. The workflow — not this command — owns the R2 conversation-transaction
/// rollback on a Fatal source-coverage failure and the R1 in-process export
/// staging/backup/restore behaviour. The CLI only maps the result to a JSON document or an
/// error envelope.
/// </para>
/// </summary>
public sealed class ExportCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;
    private readonly ArchiveWorkflow _workflow;
    private readonly SourceConversationResolver _resolver;
    private readonly CliExportDefaults _defaults;

    public ExportCommand(
        SourceCatalogService catalog,
        ArchiveWorkflow workflow,
        CliExportDefaults defaults)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
        _resolver = new SourceConversationResolver(catalog);
    }

    public string Name => "export";

    public string Description => "Export one conversation to a JSONL dataset.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var (conversation, output) = ParseArgs(args, _defaults);

        CliReporting.Progress(context, $"Resolving conversation '{conversation}'…");
        var (account, source) = await _resolver.ResolveAsync(conversation, cancellationToken)
            .ConfigureAwait(false);

        var request = new ExportConversationRequest
        {
            SourceProfileId = account.SourceProfileId,
            SourceConversationId = source.SourceConversationId,
            Kind = source.Kind,
            PeerSourceUserId = source.PeerSourceUserId,
            ConversationTitle = source.Title,
            OutputDirectory = output,
        };

        CliReporting.Progress(context, $"Exporting '{source.SourceConversationId}' to {output}…");
        var progress = new CliProgress<OperationProgress>(context, FormatProgress);
        var result = await _workflow
            .ExportConversationAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        var dto = new ExportResultDto
        {
            Succeeded = result.Succeeded,
            OutputDirectory = result.OutputDirectory,
            ConversationIds = [.. result.ConversationIds],
            RecordCount = result.RecordCount,
            UnknownCount = result.UnknownCount,
            PartialCount = result.PartialCount,
            TimeRange = new ExportTimeRangeDto
            {
                FirstMessageAt = FormatTimestamp(result.FirstMessageAt),
                LastMessageAt = FormatTimestamp(result.LastMessageAt),
            },
            Files = [.. result.Files.Select(f => new ExportFileDto
            {
                Path = f.RelativePath,
                Kind = f.Kind,
                RecordCount = f.RecordCount,
            })],
            ConversationPaths = [.. result.ConversationPaths],
            Diagnostics = [.. result.Diagnostics.Select(CliDiagnosticDto.From)],
        };

        WriteResult(context, dto);
        return ExitCode.Success;
    }

    private static (string Conversation, string Output) ParseArgs(
        IReadOnlyList<string> args,
        CliExportDefaults defaults)
    {
        string? conversation = null;
        string? output = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--conversation":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException("--conversation requires a value.");
                    conversation = args[++i];
                    break;
                case "--output":
                case "-o":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException("--output requires a value.");
                    output = args[++i];
                    break;
                default:
                    throw new CliUsageException($"unknown option '{args[i]}' for export.");
            }
        }

        if (string.IsNullOrWhiteSpace(conversation))
            throw new CliUsageException("export requires --conversation <id-or-alias>.");

        // --no-input never prompts: --output is optional and defaults to a host-supplied path.
        var resolvedOutput = string.IsNullOrWhiteSpace(output)
            ? defaults.DefaultOutputDirectory
            : output;

        return (conversation, resolvedOutput);
    }

    private static void WriteResult(CliContext context, ExportResultDto result)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
            return;
        }

        context.Stdout.WriteLine($"Exported {result.RecordCount} record(s) to {result.OutputDirectory}");
        context.Stdout.WriteLine($"  conversations: {string.Join(", ", result.ConversationIds)}");
        context.Stdout.WriteLine($"  output:         {Path.Combine(result.OutputDirectory, "manifest.json")}");
        context.Stdout.WriteLine(
            $"  coverage:       {result.UnknownCount} unknown, {result.PartialCount} partial");

        if (result.Diagnostics.Count > 0)
        {
            context.Stdout.WriteLine("  diagnostics:");
            foreach (var d in result.Diagnostics)
            {
                context.Stdout.WriteLine($"    {d.Severity} {d.Code}: {d.Message}");
            }
        }
    }

    private static string FormatProgress(OperationProgress p) =>
        p.Total > 0
            ? $"{p.Stage}: {p.Processed}/{p.Total}"
            : string.IsNullOrEmpty(p.Stage) ? string.Empty : p.Stage;

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value is null ? null : value.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
