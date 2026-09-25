using System.Text.Json;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
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
        IProgress<string>? progress, CancellationToken cancellationToken, bool replay = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var latest = await _rawVault.GetLatestGenerationAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No published Raw Vault generations are available for this account.");

        // Check canonical scope state before opening generation artifacts. Opening validates
        // every artifact hash, which is unnecessary when all existing scopes already cover
        // this immutable generation and reader version.
        var knownConversations = await _archive.ListConversationsAsync(accountId, cancellationToken).ConfigureAwait(false);
        var selectedKnown = sourceConversationId is null
            ? knownConversations
            : knownConversations.Where(c => string.Equals(c.SourceConversationId, sourceConversationId, StringComparison.Ordinal)).ToArray();
        if (!replay && selectedKnown.Count > 0)
        {
            var current = true;
            foreach (var conversation in selectedKnown)
            {
                var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                    ScopeKind, conversation.Id, cancellationToken).ConfigureAwait(false);
                var state = ReadCheckpoint(checkpoint?.CheckpointJson);
                if (state.GenerationId != latest.GenerationId
                    || state.ReaderVersion != WeChatWindowsSourceAdapter.Version)
                {
                    current = false;
                    break;
                }
            }
            if (current) return 0;
        }

        var generation = await _rawVault.OpenGenerationAsync(accountId, latest.GenerationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Raw Vault generation '{latest.GenerationId}' failed manifest or checksum validation.");
        using var adapter = CapturedWeChatSourceAdapter.Create(generation);
        var account = (await adapter.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
        if (!string.Equals(StableIds.Account(adapter.AdapterName, account.SourceProfileId), accountId, StringComparison.Ordinal))
            throw new InvalidDataException("Raw Vault account identity does not match the stable source identity.");
        var importer = new ImportService(adapter, _archive, _clock);
        var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, cancellationToken).ConfigureAwait(false);
        if (sourceConversationId is not null)
        {
            conversations = conversations.Where(c => string.Equals(c.SourceConversationId, sourceConversationId, StringComparison.Ordinal)).ToArray();
            if (conversations.Count == 0)
                throw new InvalidOperationException($"Conversation '{sourceConversationId}' was not found in Raw Vault account '{accountId}'.");
        }

        var processed = 0;
        foreach (var conversation in conversations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conversationId = StableIds.Conversation(accountId, conversation.Kind, conversation.SourceConversationId, conversation.PeerSourceUserId);
            var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, generation.Manifest.Capture.CaptureAdapterFamily,
                ScopeKind, conversationId, cancellationToken).ConfigureAwait(false);
            var state = ReadCheckpoint(checkpoint?.CheckpointJson);
            if (!replay && state.GenerationId == latest.GenerationId && state.ReaderVersion == adapter.AdapterVersion)
                continue;

            var detail = await adapter.DescribeConversationAsync(account.SourceProfileId, conversation.SourceConversationId, cancellationToken).ConfigureAwait(false);
            var nextCheckpoint = new IngestCheckpoint
            {
                Id = "ingest_" + Guid.NewGuid().ToString("N"),
                AccountId = accountId,
                AdapterFamily = generation.Manifest.Capture.CaptureAdapterFamily,
                ScopeKind = ScopeKind,
                ScopeId = conversationId,
                CheckpointJson = JsonSerializer.Serialize(new { version = 1, reader_version = adapter.AdapterVersion, generation_id = latest.GenerationId }),
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
            progress?.Report($"Ingested {conversationId} from generation {latest.GenerationId}");
        }
        return processed;
    }

    private static (string? ReaderVersion, string? GenerationId) ReadCheckpoint(string? checkpointJson)
    {
        if (checkpointJson is null) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(checkpointJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
                throw new InvalidDataException("The stored ingest checkpoint uses an unsupported format version.");
            if (!root.TryGetProperty("reader_version", out var readerVersion)
                || !root.TryGetProperty("generation_id", out var generationId))
                throw new InvalidDataException("The stored ingest checkpoint is missing required cursor fields.");
            return (readerVersion.GetString(), generationId.GetString());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The stored ingest checkpoint is not valid JSON.", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("The stored ingest checkpoint contains invalid cursor fields.", ex);
        }
    }
}
