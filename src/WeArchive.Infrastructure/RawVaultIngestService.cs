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
    private const string AccountScopeKind = "account";
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
        var generationsById = generations.ToDictionary(generation => generation.GenerationId, StringComparer.Ordinal);

        // The checkpoint catalog lets an unchanged repeat return before any generation's
        // artifacts are opened. Per-conversation fingerprints below distinguish changed source
        // records after a newer generation is opened.
        var currentReaderVersion = WeChatWindowsSourceAdapter.Version;
        var accountScanCheckpoint = await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
            AccountScopeKind, accountId, cancellationToken).ConfigureAwait(false);
        var accountScan = ReadCheckpoint(accountScanCheckpoint?.CheckpointJson);
        if (replay && accountScanCheckpoint is not null && accountScan.CoveredGenerationIds.Count > 0)
        {
            var invalidatedScan = CreateAccountScanCheckpoint(accountId, WeChatCaptureAdapter.Family,
                currentReaderVersion, [], _clock.UtcNow);
            await _archive.SetIngestCheckpointAsync(invalidatedScan, cancellationToken).ConfigureAwait(false);
            accountScan = ReadCheckpoint(invalidatedScan.CheckpointJson);
        }
        if (!replay && sourceConversationId is null && accountScan.ReaderVersion == currentReaderVersion
            && generations.All(generation => accountScan.CoveredGenerationIds.Contains(generation.GenerationId)))
            return 0;

        var processed = 0;
        var selectedConversation = sourceConversationId is null
            ? null
            : (await _archive.ListConversationsAsync(accountId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(conversation => string.Equals(conversation.SourceConversationId, sourceConversationId, StringComparison.Ordinal));
        var knownSelectedConversation = selectedConversation is not null;
        var selectedCheckpoint = selectedConversation is null
            ? null
            : await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family, ScopeKind,
                selectedConversation.Id, cancellationToken).ConfigureAwait(false);
        var selectedState = ReadCheckpoint(selectedCheckpoint?.CheckpointJson);
        if (!replay && sourceConversationId is not null && knownSelectedConversation
            && accountScan.ReaderVersion == currentReaderVersion
            && generations.All(generation => accountScan.CoveredGenerationIds.Contains(generation.GenerationId)))
            return 0;

        var foundSelectedConversation = knownSelectedConversation;
        foreach (var summary in generations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!replay)
            {
                if (accountScan.ReaderVersion == currentReaderVersion
                    && accountScan.CoveredGenerationIds.Contains(summary.GenerationId))
                    continue;
                if (sourceConversationId is not null && selectedState.ReaderVersion == currentReaderVersion
                    && IsAncestorOrSelf(generationsById, summary.GenerationId, selectedState.GenerationId))
                    continue;
            }

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
                if (!replay && state.ReaderVersion == adapter.AdapterVersion
                    && IsAncestorOrSelf(generationsById, summary.GenerationId, state.GenerationId))
                    continue;
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

            if (sourceConversationId is null)
            {
                var covered = new HashSet<string>(
                    accountScan.ReaderVersion == adapter.AdapterVersion
                        ? accountScan.CoveredGenerationIds
                        : [],
                    StringComparer.Ordinal)
                {
                    summary.GenerationId,
                };
                var scanCheckpoint = CreateAccountScanCheckpoint(accountId,
                    generation.Manifest.Capture.CaptureAdapterFamily, adapter.AdapterVersion, covered, _clock.UtcNow);
                await _archive.SetIngestCheckpointAsync(scanCheckpoint, cancellationToken).ConfigureAwait(false);
                accountScan = ReadCheckpoint(scanCheckpoint.CheckpointJson);
            }
        }

        if (sourceConversationId is not null && !foundSelectedConversation)
            throw new InvalidOperationException($"Conversation '{sourceConversationId}' was not found in Raw Vault account '{accountId}'.");
        return processed;
    }

    private static bool IsAncestorOrSelf(IReadOnlyDictionary<string, Core.RawVault.RawGenerationSummary> generations,
        string candidateGenerationId, string? descendantGenerationId)
    {
        var current = descendantGenerationId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current))
        {
            if (current == candidateGenerationId) return true;
            if (!generations.TryGetValue(current, out var summary)) return false;
            current = summary.PreviousGenerationId;
        }
        return false;
    }

    private static IngestCheckpoint CreateAccountScanCheckpoint(string accountId, string adapterFamily,
        string readerVersion, IEnumerable<string> coveredGenerationIds, DateTimeOffset updatedAt) => new()
    {
        Id = "ingest_" + Guid.NewGuid().ToString("N"),
        AccountId = accountId,
        AdapterFamily = adapterFamily,
        ScopeKind = AccountScopeKind,
        ScopeId = accountId,
        CheckpointJson = JsonSerializer.Serialize(new
        {
            version = 1,
            reader_version = readerVersion,
            generation_id = "",
            evidence_fingerprint = "complete_account_scan",
            covered_generation_ids = coveredGenerationIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        }),
        UpdatedAt = updatedAt,
    };

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

    private static (string? ReaderVersion, string? GenerationId, string? Fingerprint, HashSet<string> CoveredGenerationIds) ReadCheckpoint(string? checkpointJson)
    {
        if (checkpointJson is null) return (null, null, null, new HashSet<string>(StringComparer.Ordinal));
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
            var covered = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("covered_generation_ids", out var coveredIds))
            {
                if (coveredIds.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The account scan cursor contains invalid covered generation identities.");
                foreach (var coveredId in coveredIds.EnumerateArray())
                {
                    var id = coveredId.GetString();
                    if (string.IsNullOrWhiteSpace(id))
                        throw new InvalidDataException("The account scan cursor contains an empty generation identity.");
                    covered.Add(id);
                }
            }
            return (readerVersion.GetString(), generationId.GetString(), fingerprint.GetString(), covered);
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
