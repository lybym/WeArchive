using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Collections;
using WeArchive.Core.RawVault;

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
    private readonly CollectionSyncService? _collectionSync;

    public SyncCommand(SourceCatalogService catalog, ImportService importer, CollectionSyncService? collectionSync = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _collectionSync = collectionSync;
        _resolver = new SourceConversationResolver(catalog);
    }

    public string Name => "sync";

    public string Description => "Capture and ingest one conversation or a named collection.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var selection = ParseArgs(args);
        if (selection.Collection is not null)
        {
            if (_collectionSync is null)
                throw new CliUsageException("sync --collection is unavailable because no Collection catalog was configured.");
            CollectionSyncResult collectionResult;
            try
            {
                collectionResult = await _collectionSync.SyncAsync(selection.Collection,
                    new CliProgress<CaptureProgress>(context, item => item.Stage), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (KeyNotFoundException ex)
            {
                context.WriteError(CliErrorCode.UsageError, ex.Message);
                return ExitCode.UsageError;
            }
            catch (Exception ex)
            {
                context.WriteError(CliErrorCode.Failure, ex.Message);
                return ExitCode.Failure;
            }
            var dto = new
            {
                collection = collectionResult.CollectionName,
                succeeded = collectionResult.Succeeded,
                conversations = collectionResult.Conversations.Select(item => new
                {
                    conversation_id = item.ConversationId,
                    status = item.Status,
                    error = item.Error,
                }).ToArray(),
            };
            if (context.Options.Json) context.Stdout.WriteLine(CliJson.Serialize(dto));
            else
            {
                foreach (var item in collectionResult.Conversations)
                    context.Stdout.WriteLine($"{item.ConversationId}: {item.Status}{(item.Error is null ? string.Empty : $" — {item.Error}")}");
            }
            return collectionResult.Cancelled ? ExitCode.Cancelled
                : collectionResult.Succeeded ? ExitCode.Success : ExitCode.Failure;
        }
        var conversation = selection.Conversation!;

        CliReporting.Progress(context, $"Resolving conversation '{conversation}'…");
        var resolved = await _resolver.ResolveAsync(context, conversation, cancellationToken)
            .ConfigureAwait(false);
        if (resolved is null)
            return ExitCode.Failure;
        var (account, source) = resolved.Value;

        CliReporting.Progress(context, $"Probing '{source.SourceConversationId}'…");
        SourceConversationDetail detail;
        try
        {
            detail = await _catalog
                .DescribeConversationAsync(account.SourceProfileId, source.SourceConversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The probe only fills the progress total, but a source that fails it after the
            // conversation resolved is the same condition `conversation show` reports as
            // `conversation_describe_failed`. Surface that granular code instead of letting
            // CliHost collapse it to a generic `failure` (docs/CLI.md error table).
            context.WriteError(CliErrorCode.ConversationDescribeFailed, ex.Message);
            return ExitCode.Failure;
        }

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

    private static (string? Conversation, string? Collection) ParseArgs(IReadOnlyList<string> args)
    {
        string? conversation = null;
        string? collection = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--conversation":
                case "--collection":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException($"{args[i]} requires a value.");
                    if (args[i] == "--conversation") conversation = args[++i];
                    else collection = args[++i];
                    break;
                default:
                    throw new CliUsageException($"unknown option '{args[i]}' for sync.");
            }
        }

        if (string.IsNullOrWhiteSpace(conversation) == string.IsNullOrWhiteSpace(collection))
            throw new CliUsageException("sync requires exactly one of --conversation <id-or-alias> or --collection <name>.");

        // --no-input never prompts: the command auto-selects the current source account, so it
        // has no prompt path. Missing required input is a deterministic usage error (exit 2).
        return (conversation, collection);
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
