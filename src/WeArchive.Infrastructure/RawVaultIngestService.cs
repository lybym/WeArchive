using System.Security.Cryptography;
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
        // artifacts are opened. Per-conversation fingerprints below distinguish changed source
        // records after a newer generation is opened.
        var knownConversations = await _archive.ListConversationsAsync(accountId, cancellationToken).ConfigureAwait(false);
        var selectedKnown = SelectScopes(knownConversations, sourceConversationId);
        var currentReaderVersion = WeChatWindowsSourceAdapter.Version;
        if (!replay && selectedKnown.Count > 0
            && await AllAtLatestGenerationAsync(accountId, selectedKnown, generations[^1], currentReaderVersion, cancellationToken).ConfigureAwait(false))
            return 0;

        var processed = 0;
        var foundSelectedConversation = false;
        foreach (var summary in generations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generationIndex = IndexOfGeneration(generations, summary.GenerationId);
            if (!replay && selectedKnown.Count > 0
                && await AllPastCheckpointAsync(accountId, selectedKnown, generations, generationIndex, currentReaderVersion, cancellationToken).ConfigureAwait(false))
                continue;

            // Covered history is skipped using each conversation's committed generation cursor.
            // Newer generations are opened and validated; unchanged conversations are fingerprinted
            // but neither reimported nor advanced, while absent conversations retain old canonical data.
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
                var detail = await adapter.DescribeConversationAsync(account.SourceProfileId, conversation.SourceConversationId, cancellationToken).ConfigureAwait(false);
                var evidenceFingerprint = await FingerprintConversationAsync(adapter, account.SourceProfileId,
                    conversation, detail, cancellationToken).ConfigureAwait(false);
                if (!replay && state.ReaderVersion == adapter.AdapterVersion
                    && string.Equals(state.Fingerprint, evidenceFingerprint, StringComparison.Ordinal)) continue;

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
                        evidence_fingerprint = evidenceFingerprint,
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

    private async Task<bool> AllAtLatestGenerationAsync(string accountId, IReadOnlyList<ArchiveConversation> conversations,
        Core.RawVault.RawGenerationSummary latest, string readerVersion, CancellationToken cancellationToken)
    {
        foreach (var conversation in conversations)
        {
            var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                ScopeKind, conversation.Id, cancellationToken).ConfigureAwait(false);
            var state = ReadCheckpoint(checkpoint?.CheckpointJson);
            if (state.ReaderVersion != readerVersion || state.GenerationId != latest.GenerationId) return false;
        }
        return true;
    }

    private async Task<bool> AllPastCheckpointAsync(string accountId, IReadOnlyList<ArchiveConversation> conversations,
        IReadOnlyList<Core.RawVault.RawGenerationSummary> generations, int generationIndex, string readerVersion,
        CancellationToken cancellationToken)
    {
        foreach (var conversation in conversations)
        {
            var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                ScopeKind, conversation.Id, cancellationToken).ConfigureAwait(false);
            var state = ReadCheckpoint(checkpoint?.CheckpointJson);
            if (state.ReaderVersion != readerVersion) return false;
            var checkpointIndex = IndexOfGeneration(generations, state.GenerationId);
            if (checkpointIndex < generationIndex) return false;
        }
        return conversations.Count > 0;
    }

    private static int IndexOfGeneration(IReadOnlyList<Core.RawVault.RawGenerationSummary> generations, string? generationId)
    {
        for (var index = 0; index < generations.Count; index++)
            if (generations[index].GenerationId == generationId) return index;
        return -1;
    }

    private static async Task<string> FingerprintConversationAsync(ISourceAdapter adapter, string profileId,
        SourceConversation conversation, SourceConversationDetail detail, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFingerprintPart(hash, JsonSerializer.SerializeToUtf8Bytes(conversation));
        AppendFingerprintPart(hash, JsonSerializer.SerializeToUtf8Bytes(detail));
        await foreach (var message in adapter.ReadMessagesAsync(profileId, conversation.SourceConversationId, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
            AppendFingerprintPart(hash, JsonSerializer.SerializeToUtf8Bytes(message));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFingerprintPart(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static IReadOnlyList<ArchiveConversation> SelectScopes(
        IReadOnlyList<ArchiveConversation> conversations, string? sourceConversationId) =>
        sourceConversationId is null
            ? conversations
            : conversations.Where(c => string.Equals(c.SourceConversationId, sourceConversationId, StringComparison.Ordinal)).ToArray();

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
