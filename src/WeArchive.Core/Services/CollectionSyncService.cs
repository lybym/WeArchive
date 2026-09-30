using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Core.Services;

/// <summary>
/// Collection-scoped synchronization: resolve a named Collection's stable conversation IDs,
/// capture required live-source evidence once through the shared
/// <see cref="SyncOrchestrationService"/>, then ingest each conversation from the Raw Vault through
/// the same boundary. docs/PRD.md FR-23/FR-29/G4/G8, docs/ARCHITECTURE.md section 3.8.
/// <para>
/// Collection execution is deliberately multi-scope, not one transaction. Each conversation's
/// canonical writes and ingest checkpoint commit in that conversation's own SQLite transaction, so
/// successful conversations keep their progress and one failure never rolls back another. The
/// service therefore collects a structured per-conversation result instead of aborting the run.
/// </para>
/// <para>
/// It reuses the existing orchestration rather than adding a second one: evidence and canonical
/// publication both come from <see cref="SyncOrchestrationService"/>, the one preservation-first
/// boundary <c>sync --conversation</c> uses, so no JSONL scanning and no separate source parser is
/// introduced.
/// </para>
/// <para>
/// Reliability is unchanged by this service. It adds no journal, commit marker, recovery state or
/// new transaction protocol; it only sequences the already-documented R1 capture and R2
/// conversation ingest per member.
/// </para>
/// </summary>
public sealed class CollectionSyncService(
    CollectionCatalogService catalog,
    SyncOrchestrationService orchestration)
{
    private readonly CollectionCatalogService _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly SyncOrchestrationService _orchestration =
        orchestration ?? throw new ArgumentNullException(nameof(orchestration));

    /// <summary>
    /// Synchronizes one named Collection. The capture is account-scoped and mandatory: a
    /// Collection sync is a live-source sync, so it never silently ingests stale evidence and
    /// reports success. A capture that publishes nothing is an operation-level failure
    /// (<see cref="CollectionCaptureException"/>), not a fabricated per-conversation failure.
    /// </summary>
    /// <exception cref="CollectionConfigurationException">The Collection configuration is invalid.</exception>
    /// <exception cref="CollectionNotFoundException">The Collection name is not defined.</exception>
    /// <exception cref="CollectionCaptureException">No usable Raw Vault generation was published.</exception>
    /// <exception cref="OperationCanceledException">
    /// The run was cancelled. Conversations that already committed keep their progress; the
    /// in-flight conversation is rolled back.
    /// </exception>
    public async Task<CollectionSyncResult> SyncAsync(
        CollectionSyncRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var definition = await _catalog.ResolveAsync(request.CollectionName, cancellationToken)
            .ConfigureAwait(false);

        var items = new List<CollectionSyncItem>();

        // A declared entry that is not a stable conversation id was still requested by the user, so
        // it is reported as an unresolved member rather than dropped. The configuration file is
        // never rewritten.
        foreach (var invalid in definition.InvalidConversationIds)
        {
            items.Add(new CollectionSyncItem
            {
                ConversationId = invalid,
                Status = CollectionSyncItemStatus.Unresolved,
                Error = "The Collection declares an entry that is not a stable conversation id (g_/u_ plus 16 lowercase hex characters).",
            });
        }

        // An empty Collection has nothing to capture: running a live capture for a scope with no
        // members would acquire evidence no caller asked for.
        if (definition.ConversationIds.Count == 0)
        {
            progress?.Report($"Collection '{definition.Name}' has no resolvable conversation members.");
            return Result(definition, items, capture: null);
        }

        progress?.Report($"Capturing account evidence for collection '{definition.Name}'…");
        CaptureResult capture;
        try
        {
            capture = await _orchestration
                .CaptureAsync(request.SourceProfileId, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SyncCaptureException ex)
        {
            throw new CollectionCaptureException(definition.Name, ex.Reason);
        }

        foreach (var conversationId in definition.ConversationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Syncing {conversationId}…");

            try
            {
                var ingested = await _orchestration
                    .IngestConversationAsync(
                        capture.AccountId, conversationId, capture.GenerationId, progress, cancellationToken)
                    .ConfigureAwait(false);

                items.Add(new CollectionSyncItem
                {
                    ConversationId = conversationId,
                    // Nothing published means the member was verified and unchanged; that is a
                    // successful outcome, not a failure, and its checkpoint keeps its value.
                    Status = ingested.Status == SyncPublicationStatus.Succeeded
                        ? CollectionSyncItemStatus.Succeeded
                        : CollectionSyncItemStatus.NoChange,
                    ConversationsIngested = ingested.ConversationsPublished,
                });
            }
            catch (OperationCanceledException)
            {
                // Cancellation is a run-level stop, not a per-member outcome. Conversations that
                // already committed remain committed and their checkpoints stay advanced.
                throw;
            }
            catch (ConversationNotInRawVaultException ex)
            {
                items.Add(new CollectionSyncItem
                {
                    ConversationId = conversationId,
                    Status = CollectionSyncItemStatus.Unresolved,
                    Error = ex.Message,
                });
            }
            catch (Exception ex)
            {
                items.Add(new CollectionSyncItem
                {
                    ConversationId = conversationId,
                    Status = CollectionSyncItemStatus.Failed,
                    Error = ex.Message,
                });
            }
        }

        return Result(definition, items, capture);
    }

    private static CollectionSyncResult Result(
        CollectionDefinition definition,
        IReadOnlyList<CollectionSyncItem> items,
        CaptureResult? capture) => new()
    {
        CollectionName = definition.Name,
        Succeeded = items.All(item =>
            item.Status is CollectionSyncItemStatus.Succeeded or CollectionSyncItemStatus.NoChange),
        Items = items,
        AccountId = capture?.AccountId,
        SourceProfileId = capture?.SourceProfileId,
        GenerationId = capture?.GenerationId,
        CaptureMode = capture?.Mode,
        InvalidConversationIds = definition.InvalidConversationIds,
        DuplicateConversationIds = definition.DuplicateConversationIds,
    };
}
