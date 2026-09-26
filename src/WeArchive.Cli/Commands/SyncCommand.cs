using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive sync --conversation &lt;id-or-alias&gt;</c> and
/// <c>wearchive sync --collection &lt;name&gt;</c>: imports one conversation, or every conversation a
/// named Collection scopes, from the local source into the SQLite archive
/// (docs/PRD.md FR-04/FR-08/FR-09/FR-23/FR-29, docs/ROADMAP.md M0.5/M4).
/// <para>
/// This is a thin transport adapter. The <c>--conversation</c> path resolves the requested
/// conversation through the source catalog using the same stable upstream identifier the
/// <see cref="ImportService"/> consumes, then delegates publication entirely to
/// <see cref="ImportService"/>. The <c>--collection</c> path delegates the whole
/// capture-then-ingest scope to <see cref="CollectionSyncService"/>. This command contains no
/// normalization, no WeChat schema logic and no archive transaction semantics: a Fatal
/// source-coverage failure rolls back the whole conversation transaction inside the importer (R2).
/// </para><para>
/// Collection execution is multi-scope, not one transaction, so its result document reports each
/// conversation's own outcome and the process exits non-zero when any requested conversation failed
/// or was unresolved — a partially successful run is never presented as total success.
/// </para>
/// </summary>
public sealed class SyncCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;
    private readonly ImportService _importer;
    private readonly SourceConversationResolver _resolver;
    private readonly CollectionSyncService _collectionSync;

    public SyncCommand(
        SourceCatalogService catalog,
        ImportService importer,
        CollectionSyncService collectionSync)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _collectionSync = collectionSync ?? throw new ArgumentNullException(nameof(collectionSync));
        _resolver = new SourceConversationResolver(catalog);
    }

    public string Name => "sync";

    public string Description => "Import one conversation or a Collection into the archive.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var parsed = ParseArgs(args);

        return parsed.Collection is not null
            ? await SyncCollectionAsync(context, parsed.Collection, cancellationToken).ConfigureAwait(false)
            : await SyncConversationAsync(context, parsed.Conversation!, cancellationToken).ConfigureAwait(false);
    }

    // ---- sync --conversation -----------------------------------------------

    private async Task<int> SyncConversationAsync(
        CliContext context,
        string conversation,
        CancellationToken cancellationToken)
    {
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

    // ---- sync --collection -------------------------------------------------

    private async Task<int> SyncCollectionAsync(
        CliContext context,
        string collection,
        CancellationToken cancellationToken)
    {
        CliReporting.Progress(context, $"Synchronizing collection '{collection}'…");

        CollectionSyncResult outcome;
        try
        {
            outcome = await _collectionSync
                .SyncAsync(
                    new CollectionSyncRequest { CollectionName = collection },
                    new HumanProgress(context),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a run-level stop, not a per-member outcome: conversations that
            // already committed keep their progress and their checkpoints, and the in-flight
            // conversation was rolled back.
            context.WriteError(
                CliErrorCode.Cancelled,
                "sync --collection was cancelled; completed conversations keep their progress.");
            return ExitCode.Cancelled;
        }
        catch (CollectionConfigurationException ex)
        {
            context.WriteError(CliErrorCode.CollectionConfigInvalid, ex.Message);
            return ExitCode.UsageError;
        }
        catch (CollectionNotFoundException ex)
        {
            context.WriteError(CliErrorCode.CollectionNotFound, ex.Message);
            return ExitCode.Failure;
        }
        catch (CollectionCaptureException ex)
        {
            // Capture is account-scoped and mandatory for a Collection sync, so a capture that
            // publishes nothing fails the operation instead of fabricating per-member failures.
            context.WriteError(CliErrorCode.CaptureFailed, ex.Message);
            return ExitCode.Failure;
        }

        var result = new CollectionSyncResultDto
        {
            Collection = outcome.CollectionName,
            Succeeded = outcome.Succeeded,
            AccountId = outcome.AccountId,
            SourceProfileId = outcome.SourceProfileId,
            GenerationId = outcome.GenerationId,
            CaptureMode = outcome.CaptureMode?.ToString().ToLowerInvariant(),
            Conversations = [.. outcome.Items.Select(CollectionSyncItemDto.From)],
            Summary = new CollectionSyncSummaryDto
            {
                Requested = outcome.Items.Count,
                Succeeded = outcome.SucceededCount,
                NoChange = outcome.NoChangeCount,
                Failed = outcome.FailedCount,
            },
            InvalidConversationIds = outcome.InvalidConversationIds,
            DuplicateConversationIds = outcome.DuplicateConversationIds,
        };

        // The structured result is written on every path, including partial failure: a machine
        // caller must still see which members succeeded, while the process exit code stays the
        // authoritative outcome class (docs/CLI.md, docs/ARCHITECTURE.md section 3.1.1).
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
        }
        else
        {
            WriteCollectionResult(context, result);
        }

        return result.Succeeded ? ExitCode.Success : ExitCode.Failure;
    }

    // ---- argument parsing --------------------------------------------------

    private sealed record SyncArguments(string? Conversation, string? Collection);

    private static SyncArguments ParseArgs(IReadOnlyList<string> args)
    {
        string? conversation = null;
        string? collection = null;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--conversation":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException("--conversation requires a value.");
                    conversation = args[++i];
                    break;
                case "--collection":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException("--collection requires a value.");
                    collection = args[++i];
                    break;
                default:
                    throw new CliUsageException($"unknown option '{args[i]}' for sync.");
            }
        }

        if (conversation is not null && collection is not null)
            throw new CliUsageException("sync accepts either --conversation <id-or-alias> or --collection <name>, not both.");

        if (string.IsNullOrWhiteSpace(conversation) && string.IsNullOrWhiteSpace(collection))
            throw new CliUsageException("sync requires --conversation <id-or-alias> or --collection <name>.");

        // --no-input never prompts: both selectors are explicit and the account is auto-selected,
        // so the command has no prompt path. Missing required input is a usage error (exit 2).
        return new SyncArguments(
            string.IsNullOrWhiteSpace(conversation) ? null : conversation,
            string.IsNullOrWhiteSpace(collection) ? null : collection);
    }

    // ---- rendering ---------------------------------------------------------

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

    private static void WriteCollectionResult(CliContext context, CollectionSyncResultDto result)
    {
        context.Stdout.WriteLine($"Synced collection {result.Collection}");
        if (result.SourceProfileId is not null)
            context.Stdout.WriteLine($"  account:  {result.SourceProfileId}");
        if (result.GenerationId is not null)
            context.Stdout.WriteLine($"  generation: {result.GenerationId} ({result.CaptureMode})");

        foreach (var item in result.Conversations)
        {
            var suffix = string.IsNullOrEmpty(item.Error) ? string.Empty : $" — {item.Error}";
            context.Stdout.WriteLine($"  {item.ConversationId,-20} {item.Status}{suffix}");
        }

        context.Stdout.WriteLine(
            $"  summary: {result.Summary.Succeeded} succeeded, {result.Summary.NoChange} unchanged, {result.Summary.Failed} failed of {result.Summary.Requested}");
    }

    private static string FormatProgress(OperationProgress p) =>
        p.Total > 0
            ? $"{p.Stage}: {p.Processed}/{p.Total}"
            : string.IsNullOrEmpty(p.Stage) ? string.Empty : p.Stage;

    private static string? FormatTimestamp(DateTimeOffset? value) =>
        value is null ? null : value.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Forwards application progress to the human diagnostics stream synchronously (unlike
    /// <see cref="Progress{T}"/>, which posts asynchronously), so progress lines stay ordered and
    /// cannot be written after the command has returned.
    /// </summary>
    private sealed class HumanProgress(CliContext context) : IProgress<string>
    {
        public void Report(string value) => context.ReportProgress(value);
    }
}
