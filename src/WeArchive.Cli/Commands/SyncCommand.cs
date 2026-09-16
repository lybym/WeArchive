using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive sync --conversation &lt;id-or-alias&gt;</c>: imports one conversation from
/// the local source into the SQLite archive (docs/PRD.md FR-04/FR-08/FR-09, ROADMAP M0.5).
/// <para>
/// This is a thin transport adapter. It resolves the requested conversation through the
/// source catalog using the same stable upstream identifier the <see cref="ImportService"/>
/// consumes, then delegates publication entirely to <see cref="ImportService"/>. It contains
/// no normalization, no WeChat schema logic and no archive transaction semantics: a Fatal
/// source-coverage failure rolls back the whole conversation transaction inside the importer
/// (R2), and the CLI only maps the outcome to a result or an error document.
/// </para>
/// </summary>
public sealed class SyncCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;
    private readonly ImportService _importer;
    private readonly SourceConversationResolver _resolver;

    public SyncCommand(SourceCatalogService catalog, ImportService importer)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _resolver = new SourceConversationResolver(catalog);
    }

    public string Name => "sync";

    public string Description => "Import one conversation into the archive.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var conversation = ParseArgs(args);

        CliReporting.Progress(context, $"Resolving conversation '{conversation}'…");
        var (account, source) = await _resolver.ResolveAsync(conversation, cancellationToken)
            .ConfigureAwait(false);

        CliReporting.Progress(context, $"Probing '{source.SourceConversationId}'…");
        var detail = await _catalog
            .DescribeConversationAsync(account.SourceProfileId, source.SourceConversationId, cancellationToken)
            .ConfigureAwait(false);

        var request = new ImportRequest
        {
            SourceProfileId = account.SourceProfileId,
            SourceConversationId = source.SourceConversationId,
            Kind = source.Kind,
            PeerSourceUserId = source.PeerSourceUserId,
            ConversationTitle = source.Title,
            TotalHint = detail.MessageCount,
        };

        var progress = new CliProgress<OperationProgress>(context, FormatProgress);
        var outcome = await _importer
            .ImportConversationAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        // A Fatal source-coverage failure is returned (not thrown) by ImportService with
        // Status=Failed after the whole conversation transaction was rolled back (R2, FR-14).
        // The CLI surfaces it as a runtime failure (exit 1); the archive keeps the exact state
        // it had before the run, so no partial conversation is published.
        if (outcome.Run.Status != ImportRunStatus.Completed)
        {
            var fatal = outcome.Diagnostics.LastOrDefault(d => d.Severity == DiagnosticSeverity.Fatal);
            var message = fatal?.Message
                ?? $"Import did not complete (status: {outcome.Run.Status}).";
            context.WriteError(CliErrorCode.Failure, message);
            return ExitCode.Failure;
        }

        var accountId = StableIds.Account(_catalog.AdapterName, account.SourceProfileId);
        var result = new SyncResultDto
        {
            ConversationId = outcome.ConversationId,
            AccountId = accountId,
            SourceProfileId = account.SourceProfileId,
            SourceConversationId = source.SourceConversationId,
            RecordsScanned = outcome.Run.RecordsScanned,
            Counters = new SyncCountersDto
            {
                Inserted = outcome.Run.RecordsInserted,
                Updated = outcome.Run.RecordsUpdated,
                Unchanged = outcome.Run.RecordsSkipped,
                Unknown = outcome.Run.UnknownCount,
                Partial = outcome.Run.PartialCount,
            },
            FirstMessageAt = FormatTimestamp(outcome.FirstMessageAt),
            LastMessageAt = FormatTimestamp(outcome.LastMessageAt),
            Diagnostics = [.. outcome.Diagnostics.Select(CliDiagnosticDto.From)],
        };

        WriteResult(context, result, accountId);
        return ExitCode.Success;
    }

    private static string ParseArgs(IReadOnlyList<string> args)
    {
        string? conversation = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--conversation":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException("--conversation requires a value.");
                    conversation = args[++i];
                    break;
                default:
                    throw new CliUsageException($"unknown option '{args[i]}' for sync.");
            }
        }

        if (string.IsNullOrWhiteSpace(conversation))
            throw new CliUsageException("sync requires --conversation <id-or-alias>.");

        // --no-input never prompts: the command auto-selects the current source account, so it
        // has no prompt path. Missing required input is a deterministic usage error (exit 2).
        return conversation;
    }

    private static void WriteResult(CliContext context, SyncResultDto result, string accountId)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
            return;
        }

        context.Stdout.WriteLine($"Synced conversation {result.ConversationId}");
        context.Stdout.WriteLine($"  account:  {accountId}");
        context.Stdout.WriteLine(
            $"  records:  {result.Counters.Inserted} new, {result.Counters.Updated} updated, {result.Counters.Unchanged} unchanged");
        context.Stdout.WriteLine(
            $"  coverage: {result.Counters.Unknown} unknown, {result.Counters.Partial} partial");

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
