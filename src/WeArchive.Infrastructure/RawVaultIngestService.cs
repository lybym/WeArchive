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
        var generations = await _rawVault.ListGenerationsAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (generations.Count == 0)
            throw new InvalidOperationException("No published Raw Vault generations are available for this account.");

        // The checkpoint catalog lets an unchanged repeat return before any generation's
        // artifacts are opened or hashed. Manifest fingerprints reveal whether evidence changed
        // without parsing private conversation data or touching artifact files.
        var knownConversations = await _archive.ListConversationsAsync(accountId, cancellationToken).ConfigureAwait(false);
        var selectedKnown = SelectScopes(knownConversations, sourceConversationId);
        var currentReaderVersion = WeChatWindowsSourceAdapter.Version;
        if (!replay && selectedKnown.Count > 0
            && await AllCoveredAsync(accountId, selectedKnown, generations[^1], currentReaderVersion, cancellationToken).ConfigureAwait(false))
            return 0;

        var processed = 0;
        var foundSelectedConversation = false;
        foreach (var summary in generations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!replay && selectedKnown.Count > 0
                && await AllCoveredAsync(accountId, selectedKnown, summary, currentReaderVersion, cancellationToken).ConfigureAwait(false))
                continue;

            // Only generations with a new manifest evidence fingerprint (or explicit replay)
            // are opened and checksum-validated. Older immutable evidence remains available for
            // conversations absent from later captures, while unchanged history stays untouched.
            var generation = await _rawVault.OpenGenerationAsync(accountId, summary.GenerationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Raw Vault generation '{summary.GenerationId}' failed manifest or checksum validation.");
            using var adapter = CapturedWeChatSourceAdapter.Create(generation);
            var account = (await adapter.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
            if (!string.Equals(StableIds.Account(adapter.AdapterName, account.SourceProfileId), accountId, StringComparison.Ordinal))
                throw new InvalidDataException("Raw Vault account identity does not match the stable source identity.");
            var importer = new ImportService(adapter, _archive, _clock);
            var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, cancellationToken).ConfigureAwait(false);
            if (sourceConversationId is not null)
                conversations = conversations.Where(c => string.Equals(c.SourceConversationId, sourceConversationId, StringComparison.Ordinal)).ToArray();

            foreach (var conversation in conversations)
            {
                foundSelectedConversation = true;
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = StableIds.Conversation(accountId, conversation.Kind, conversation.SourceConversationId, conversation.PeerSourceUserId);
                var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, generation.Manifest.Capture.CaptureAdapterFamily,
                    ScopeKind, conversationId, cancellationToken).ConfigureAwait(false);
                var state = ReadCheckpoint(checkpoint?.CheckpointJson);
                if (!replay && IsCovered(state, summary, adapter.AdapterVersion)) continue;

                var detail = await adapter.DescribeConversationAsync(account.SourceProfileId, conversation.SourceConversationId, cancellationToken).ConfigureAwait(false);
                var nextCheckpoint = new IngestCheckpoint
                {
                    Id = "ingest_" + Guid.NewGuid().ToString("N"),
                    AccountId = accountId,
                    AdapterFamily = generation.Manifest.Capture.CaptureAdapterFamily,
                    ScopeKind = ScopeKind,
                    ScopeId = conversationId,
                    CheckpointJson = JsonSerializer.Serialize(new
                    {
                        version = 1,
                        reader_version = adapter.AdapterVersion,
                        generation_id = summary.GenerationId,
                        evidence_fingerprint = summary.EvidenceFingerprint,
                    }),
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

    private async Task<bool> AllCoveredAsync(string accountId, IReadOnlyList<ArchiveConversation> conversations,
        Core.RawVault.RawGenerationSummary generation, string readerVersion, CancellationToken cancellationToken)
    {
        foreach (var conversation in conversations)
        {
            var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                ScopeKind, conversation.Id, cancellationToken).ConfigureAwait(false);
            if (!IsCovered(ReadCheckpoint(checkpoint?.CheckpointJson), generation, readerVersion)) return false;
        }
        return true;
    }

    private static IReadOnlyList<ArchiveConversation> SelectScopes(
        IReadOnlyList<ArchiveConversation> conversations, string? sourceConversationId) =>
        sourceConversationId is null
            ? conversations
            : conversations.Where(c => string.Equals(c.SourceConversationId, sourceConversationId, StringComparison.Ordinal)).ToArray();

    private static bool IsCovered((string? ReaderVersion, string? GenerationId, string? Fingerprint) state,
        Core.RawVault.RawGenerationSummary generation, string readerVersion) =>
        state.ReaderVersion == readerVersion && state.Fingerprint == generation.EvidenceFingerprint;

    private static (string? ReaderVersion, string? GenerationId, string? Fingerprint) ReadCheckpoint(string? checkpointJson)
    {
        if (checkpointJson is null) return (null, null, null);
        try
        {
            using var document = JsonDocument.Parse(checkpointJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
                throw new InvalidDataException("The stored ingest checkpoint uses an unsupported format version.");
            if (!root.TryGetProperty("reader_version", out var readerVersion)
                || !root.TryGetProperty("generation_id", out var generationId)
                || !root.TryGetProperty("evidence_fingerprint", out var fingerprint))
                throw new InvalidDataException("The stored ingest checkpoint is missing required cursor fields.");
            return (readerVersion.GetString(), generationId.GetString(), fingerprint.GetString());
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
