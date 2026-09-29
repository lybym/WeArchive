using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive sync --conversation &lt;id-or-alias&gt;</c> and
/// <c>wearchive sync --collection &lt;name&gt;</c>: synchronizes one conversation, or every
/// conversation a named Collection scopes, from the local source into the SQLite archive
/// (docs/PRD.md FR-04/FR-08/FR-09/FR-14/FR-23/FR-29, docs/ROADMAP.md M0.5/M1/M4).
/// <para>
/// This is a thin transport adapter. Both selectors resolve through the same stable-id /
/// upstream-id matching <c>conversation show</c> uses, and both publish through the one
/// preservation-first workflow: <see cref="ConversationSyncService"/> and
/// <see cref="CollectionSyncService"/> delegate live capture, Raw Vault generation selection and
/// conversation-scoped incremental ingest to the shared application boundary. The command contains
/// no normalization, no WeChat schema logic, no capture policy and no archive transaction
/// semantics: a Fatal ingest failure rolls the conversation transaction back inside the importer
/// (R2).
/// </para><para>
/// Collection execution is multi-scope, not one transaction, so its result document reports each
/// conversation's own outcome and the process exits non-zero when any requested conversation failed
/// or was unresolved — a partially successful run is never presented as total success.
/// </para>
/// </summary>
public sealed class SyncCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;
    private readonly ConversationSyncService _conversationSync;
    private readonly SourceConversationResolver _resolver;
    private readonly CollectionSyncService _collectionSync;

    public SyncCommand(
        SourceCatalogService catalog,
        ConversationSyncService conversationSync,
        CollectionSyncService collectionSync)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _conversationSync = conversationSync ?? throw new ArgumentNullException(nameof(conversationSync));
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

        var accountId = StableIds.Account(_catalog.AdapterName, account.SourceProfileId);
        var conversationId = StableIds.Conversation(
            accountId, source.Kind, source.SourceConversationId, source.PeerSourceUserId);

        CliReporting.Progress(context, $"Synchronizing {conversationId} from the live source…");

        ConversationSyncResult outcome;
        try
        {
            outcome = await _conversationSync
                .SyncAsync(
                    new ConversationSyncRequest
                    {
                        // Pinning the profile the resolver selected keeps capture and ingest on the
                        // same account the selector resolved against (no prompt, safe under
                        // --no-input).
                        SourceProfileId = account.SourceProfileId,
                        // Canonical identity is the stable conversation id, not the mutable alias
                        // the caller typed (docs/DATA_MODEL.md section 16).
                        ConversationId = conversationId,
                    },
                    new HumanProgress(context),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation follows the phase that was in flight: a cancelled capture publishes
            // nothing, and a cancelled ingest rolls back the in-flight conversation together with
            // its ingest checkpoint. A successfully published Raw Vault generation is retained
            // (docs/DEVELOPMENT.md section 10, docs/CLI.md).
            context.WriteError(
                CliErrorCode.Cancelled,
                "sync --conversation was cancelled; the in-flight conversation was rolled back and any published Raw Vault generation is retained.");
            return ExitCode.Cancelled;
        }
        catch (SyncCaptureException ex)
        {
            // Capture is account-scoped and mandatory: a capture that publishes nothing is an
            // operation-level failure, never a fabricated per-conversation result.
            context.WriteError(CliErrorCode.CaptureFailed, ex.Message);
            return ExitCode.Failure;
        }
        catch (IncompleteCanonicalCoverageException ex)
        {
            // The verified generation's evidence is not complete, so the R2 ingest refused to
            // publish a canonical result that could be mistaken for a complete one
            // (docs/PRD.md FR-20, docs/RAW_VAULT.md section 7). The failure document carries the
            // source-neutral coverage rollup so a machine caller can distinguish an incomplete
            // read from other failures (docs/CLI.md, Issue #51).
            context.WriteError(
                CliErrorCode.IncompleteCoverage,
                ex.Message,
                CanonicalCoverageDto.From(ex.Coverage));
            return ExitCode.Failure;
        }
        catch (ConversationNotInRawVaultException ex)
        {
            context.WriteError(CliErrorCode.ConversationNotFound, ex.Message);
            return ExitCode.Failure;
        }

        // Capture-side findings (a full-snapshot fallback, a completeness downgrade, …) are human
        // diagnostics on stderr. They are never part of the stdout machine document; the canonical
        // coverage contract itself is the `canonical_coverage` field of the result document
        // (docs/CLI.md, Issue #51).
        foreach (var diagnostic in outcome.CaptureDiagnostics)
        {
            CliReporting.Progress(
                context, $"capture {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        var result = new SyncResultDto
        {
            ConversationId = outcome.ConversationId,
            AccountId = outcome.AccountId,
            SourceProfileId = outcome.SourceProfileId,
            SourceConversationId = source.SourceConversationId,
            Status = FormatStatus(outcome.Status),
            ConversationsIngested = outcome.ConversationsIngested,
            GenerationId = outcome.GenerationId,
            CaptureMode = outcome.CaptureMode.ToString().ToLowerInvariant(),
            PreviousGenerationId = outcome.PreviousGenerationId,
            CanonicalCoverage = CanonicalCoverageDto.From(outcome.Coverage),
        };

        WriteResult(context, result);
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
        catch (SyncCaptureException ex)
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

    private static void WriteResult(CliContext context, SyncResultDto result)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
            return;
        }

        context.Stdout.WriteLine($"Synced conversation {result.ConversationId}");
        context.Stdout.WriteLine($"  account:    {result.AccountId} ({result.SourceProfileId})");
        context.Stdout.WriteLine($"  source:     {result.SourceConversationId}");
        context.Stdout.WriteLine($"  generation: {result.GenerationId} ({result.CaptureMode})");
        var coverage = result.CanonicalCoverage;
        context.Stdout.WriteLine(
            $"  coverage:   {coverage.Verdict} ({coverage.Expected} expected, {coverage.Available} available, " +
            $"{coverage.Unavailable} unavailable, {coverage.KnownUnsupported} known unsupported, " +
            $"{coverage.Unclassified} unclassified)");
        context.Stdout.WriteLine(
            result.Status == FormatStatus(SyncPublicationStatus.NoChange)
                ? "  result:     no_change (evidence already covered; nothing republished)"
                : $"  result:     succeeded ({result.ConversationsIngested} conversation published)");
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

    /// <summary>Stable wire name of a publication outcome, shared by the JSON and human renderings.</summary>
    private static string FormatStatus(SyncPublicationStatus status) =>
        status == SyncPublicationStatus.NoChange ? "no_change" : "succeeded";

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
