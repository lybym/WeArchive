using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;

namespace WeArchive.Infrastructure.Collections;

public sealed record CollectionConversationResult(string ConversationId, string Status, string? Error = null);
public sealed record CollectionSyncResult(string CollectionName, IReadOnlyList<CollectionConversationResult> Conversations, bool Cancelled = false)
{
    public bool Succeeded => Conversations.All(item => item.Status is "succeeded" or "no_change");
}

/// <summary>Coordinates one shared capture with independently checkpointed conversation ingests.</summary>
public sealed class CollectionSyncService(
    CollectionCatalogService collections,
    SourceCatalogService sources,
    CaptureService capture,
    RawVaultIngestService ingest)
{
    public async Task<CollectionSyncResult> SyncAsync(string name, IProgress<CaptureProgress>? progress, CancellationToken cancellationToken)
    {
        var collection = await collections.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        if (collection.ConversationIds.Count == 0)
            return new CollectionSyncResult(collection.Name, []);
        var descriptor = await sources.DescribeSourceAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.IsAvailable)
            throw new InvalidOperationException(descriptor.UnavailableReason ?? "The source is not available.");
        var accounts = await sources.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        var account = accounts.FirstOrDefault(item => item.IsCurrent) ?? accounts.FirstOrDefault()
            ?? throw new InvalidOperationException("No source profile is available.");
        var conversations = await sources.ListConversationsAsync(account.SourceProfileId, cancellationToken).ConfigureAwait(false);
        var accountId = StableIds.Account(sources.AdapterName, account.SourceProfileId);
        var byId = conversations.ToDictionary(
            item => StableIds.Conversation(accountId, item.Kind, item.SourceConversationId, item.PeerSourceUserId),
            StringComparer.Ordinal);

        var captureResult = await capture.CaptureAccountAsync(
            new CaptureRequest { SourceProfileId = account.SourceProfileId }, progress, cancellationToken).ConfigureAwait(false);
        if (!captureResult.Succeeded)
        {
            var reason = captureResult.FailureMessage ?? captureResult.Diagnostics.LastOrDefault()?.Message ?? "Live-source capture failed.";
            return new CollectionSyncResult(collection.Name, collection.ConversationIds
                .Select(id => new CollectionConversationResult(id, "failed", reason)).ToArray());
        }

        var results = new List<CollectionConversationResult>();
        for (var index = 0; index < collection.ConversationIds.Count; index++)
        {
            var conversationId = collection.ConversationIds[index];
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(new(conversationId, "cancelled", "Collection sync was cancelled."));
                results.AddRange(collection.ConversationIds.Skip(index + 1).Select(id => new CollectionConversationResult(id, "not_attempted")));
                return new CollectionSyncResult(collection.Name, results, Cancelled: true);
            }
            if (!byId.TryGetValue(conversationId, out var sourceConversation))
            {
                results.Add(new(conversationId, "unresolved_member", "Stable conversation ID is not present in the selected source profile."));
                continue;
            }
            try
            {
                var count = await ingest.IngestAsync(accountId, sourceConversation.SourceConversationId,
                    null, cancellationToken).ConfigureAwait(false);
                results.Add(new(conversationId, count == 0 ? "no_change" : "succeeded"));
            }
            catch (OperationCanceledException)
            {
                results.Add(new(conversationId, "cancelled", "Collection sync was cancelled."));
                results.AddRange(collection.ConversationIds.Skip(index + 1).Select(id => new CollectionConversationResult(id, "not_attempted")));
                return new CollectionSyncResult(collection.Name, results, Cancelled: true);
            }
            catch (Exception ex)
            {
                results.Add(new(conversationId, "failed", ex.Message));
            }
        }
        return new CollectionSyncResult(collection.Name, results);
    }
}
