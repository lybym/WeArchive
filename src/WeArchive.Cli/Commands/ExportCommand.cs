using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive export --conversation &lt;id-or-alias&gt;</c>: publishes one archived conversation's
/// JSONL dataset (docs/PRD.md FR-12, docs/EXPORT_PRD.md, ROADMAP M0.5).
/// <para>
/// This is a thin transport adapter over the canonical-archive-only export boundary (Issue #66).
/// The selector resolves from the canonical archive through <see cref="ArchiveConversationResolver"/>,
/// and the whole <c>Canonical SQLite -&gt; JSONL/YAML/JSON</c> derivation is delegated to
/// <see cref="ArchiveWorkflow"/>. No live WeChat/source-adapter access, capture, Raw Vault ingest,
/// implicit import or canonical mutation is reachable from this command: refreshing canonical state
/// is <c>sync</c>'s job, and the command only maps the result to a JSON document or an error
/// envelope. The workflow — not this command — owns the R1 in-process export
/// staging/backup/restore behaviour.
/// </para>
/// </summary>
public sealed class ExportCommand : ICliCommand
{
    private readonly ArchiveConversationResolver _resolver;
    private readonly ArchiveWorkflow _workflow;
    private readonly CliExportDefaults _defaults;

    public ExportCommand(
        ArchiveConversationResolver resolver,
        ArchiveWorkflow workflow,
        CliExportDefaults defaults)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
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

        var (conversation, output) = ParseArgs(args);

        CliReporting.Progress(context, $"Resolving conversation '{conversation}' in the canonical archive…");
        var resolved = await _resolver.ResolveAsync(conversation, cancellationToken).ConfigureAwait(false);
        switch (resolved.Status)
        {
            case ArchiveConversationResolutionStatus.NotFound:
                context.WriteError(
                    CliErrorCode.ConversationNotFound,
                    $"conversation '{conversation}' was not found in the canonical archive. " +
                    "Sync it before exporting.");
                return ExitCode.Failure;

            case ArchiveConversationResolutionStatus.Ambiguous:
                // An archived upstream source id that exists under more than one account is
                // ambiguous: the caller must use the canonical stable id. Reporting the ordered
                // candidate list keeps the failure deterministic and never falls back to the live
                // source or an arbitrary account (Issue #66, Gap E).
                context.WriteError(
                    CliErrorCode.ConversationAmbiguous,
                    $"conversation selector '{conversation}' matches {resolved.Candidates.Count} archived " +
                    $"conversations ({string.Join(", ", resolved.Candidates.Select(c => c.Id))}); " +
                    "use the canonical stable conversation id.");
                return ExitCode.Failure;
        }

        var archived = resolved.Conversation!;

        // --output is a single-conversation package root: the exporter rebuilds manifest.json,
        // conversations.yaml and identities.yaml from this invocation's conversation only, so a
        // shared root would de-index a previously exported conversation. When the caller does
        // not name a root, give each conversation its own stable-id root so the default
        // destination is always a self-consistent, standalone package and exporting one
        // conversation can never damage another's output (docs/EXPORT_PRD.md section 3.2,
        // docs/CLI.md "export").
        var resolvedOutput = output ?? Path.Combine(_defaults.DefaultOutputDirectory, archived.Id);

        var request = new ExportConversationRequest
        {
            ConversationId = archived.Id,
            OutputDirectory = resolvedOutput,
        };

        CliReporting.Progress(context, $"Exporting '{archived.Id}' to {resolvedOutput}…");
        var progress = new CliProgress<OperationProgress>(context, FormatProgress);
        var result = await _workflow
            .ExportConversationAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        // A Core exporter contract allows Succeeded=false without throwing. That must never be
        // reported as a successful process: surface the documented failure document (exit 1)
        // instead of a result document that claims a package was published.
        if (!result.Succeeded)
        {
            context.WriteError(
                CliErrorCode.Failure,
                result.FailureReason ?? "The export did not complete; no package was published.");
            return ExitCode.Failure;
        }

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

    /// <summary>
    /// Parses the command options. <c>--output</c> stays <c>null</c> when omitted because the
    /// default destination depends on the resolved conversation's stable id.
    /// </summary>
    private static (string Conversation, string? Output) ParseArgs(IReadOnlyList<string> args)
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

        // --no-input never prompts: --output is optional and defaults to a host-supplied root.
        return (conversation, string.IsNullOrWhiteSpace(output) ? null : output);
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