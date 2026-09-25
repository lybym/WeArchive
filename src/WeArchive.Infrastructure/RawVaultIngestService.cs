using System.Text.Json;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Infrastructure;

/// <summary>Incrementally publishes verified Raw Vault evidence into the canonical archive.</summary>
public sealed class RawVaultIngestService(IRawVaultStore rawVault, IArchiveStore archive, IClock clock)
{
    private const string ScopeKind = "conversation";
    private readonly IRawVaultStore _rawVault = rawVault ?? throw new ArgumentNullException(nameof(rawVault));
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<int> IngestAsync(string accountId, string? sourceConversationId,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var generations = await _rawVault.ListGenerationsAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (generations.Count == 0) throw new InvalidOperationException("No published Raw Vault generations are available for this account.");
        var latestGeneration = await _rawVault.OpenGenerationAsync(accountId, generations[^1].GenerationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Raw Vault generation '{generations[^1].GenerationId}' failed manifest or checksum validation.");
        var latestReaderVersion = latestGeneration.Manifest.Source.AdapterVersion;

        var processed = 0;
        var foundSelectedConversation = false;
        foreach (var summary in generations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = await _rawVault.OpenGenerationAsync(accountId, summary.GenerationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Raw Vault generation '{summary.GenerationId}' failed manifest or checksum validation.");
            using var adapter = CapturedWeChatSourceAdapter.Create(generation);
            var account = (await adapter.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
            if (!string.Equals(StableIds.Account(adapter.AdapterName, account.SourceProfileId), accountId, StringComparison.Ordinal))
                throw new InvalidDataException("Raw Vault account identity does not match the stable source identity.");
            var descriptor = await adapter.DescribeSourceAsync(cancellationToken).ConfigureAwait(false);
            var importer = new ImportService(adapter, _archive, _clock);
            var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, cancellationToken).ConfigureAwait(false);
            if (sourceConversationId is not null)
            {
                conversations = conversations.Where(c => string.Equals(c.SourceConversationId, sourceConversationId, StringComparison.Ordinal)).ToArray();
            }

            foreach (var conversation in conversations)
            {
                foundSelectedConversation = true;
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = StableIds.Conversation(accountId, conversation.Kind, conversation.SourceConversationId, conversation.PeerSourceUserId);
                var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, generation.Manifest.Capture.CaptureAdapterFamily,
                    ScopeKind, conversationId, cancellationToken).ConfigureAwait(false);
                var checkpointState = ReadCheckpoint(checkpoint?.CheckpointJson);
                var checkpointGeneration = string.Equals(checkpointState.ReaderVersion, latestReaderVersion, StringComparison.Ordinal)
                    ? checkpointState.GenerationId
                    : null;
                var checkpointIndex = checkpointGeneration is null ? -1 : IndexOf(generations, checkpointGeneration);
                var currentIndex = IndexOf(generations, summary.GenerationId);
                if (checkpointIndex >= currentIndex) continue;

                var detail = await adapter.DescribeConversationAsync(account.SourceProfileId, conversation.SourceConversationId, cancellationToken).ConfigureAwait(false);
                var nextCheckpoint = new IngestCheckpoint
                {
                    Id = "ingest_" + Guid.NewGuid().ToString("N"),
                    AccountId = accountId,
                    AdapterFamily = generation.Manifest.Capture.CaptureAdapterFamily,
                    ScopeKind = ScopeKind,
                    ScopeId = conversationId,
                    CheckpointJson = JsonSerializer.Serialize(new { version = 1, reader_version = descriptor.AdapterVersion, generation_id = summary.GenerationId }),
                    UpdatedAt = _clock.UtcNow,
                };
                var outcome = await importer.ImportConversationAsync(new ImportRequest
                {
                    SourceProfileId = account.SourceProfileId,
                    SourceConversationId = conversation.SourceConversationId,
                    Kind = conversation.Kind,
                    PeerSourceUserId = conversation.PeerSourceUserId,
                    ConversationTitle = conversation.Title,
                    TotalHint = detail.MessageCount,
                    IngestCheckpoint = nextCheckpoint,
                    RollbackOnCancellation = true,
                }, null, cancellationToken).ConfigureAwait(false);
                if (outcome.Run.Status != ImportRunStatus.Completed)
                    throw new InvalidDataException($"Ingest failed for conversation scope '{conversationId}'.");
                processed++;
                progress?.Report($"Ingested {conversationId} from generation {summary.GenerationId}");
            }
        }
        if (sourceConversationId is not null && !foundSelectedConversation)
            throw new InvalidOperationException($"Conversation '{sourceConversationId}' was not found in Raw Vault account '{accountId}'.");
        return processed;
    }

    private static (string? ReaderVersion, string? GenerationId) ReadCheckpoint(string? checkpointJson)
    {
        if (checkpointJson is null) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(checkpointJson);
            var root = document.RootElement;
            return (root.TryGetProperty("reader_version", out var version) ? version.GetString() : null,
                root.TryGetProperty("generation_id", out var generation) ? generation.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { return (null, null); }
    }

    private static int IndexOf(IReadOnlyList<RawGenerationSummary> generations, string id)
    {
        for (var i = 0; i < generations.Count; i++)
            if (string.Equals(generations[i].GenerationId, id, StringComparison.Ordinal)) return i;
        return -1;
    }
}
