using System.Security.Cryptography;
using System.Text.Json;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Infrastructure;

/// <summary>Incrementally publishes verified Raw Vault evidence into the canonical archive.</summary>
public sealed class RawVaultIngestService(IRawVaultStore rawVault, IArchiveStore archive, IClock clock)
    : IConversationIngestService, IIngestProgressSource
{
    /// <summary>
    /// Scope kind of a conversation's canonical content cursor. It advances only when that
    /// conversation's evidence changed and was published (docs/DATA_MODEL.md section 14.1).
    /// <para>
    /// The scope vocabulary and the cursor payload are private to this component on purpose: the
    /// canonical archive store only enumerates the rows it persists, so the checkpoint encoding
    /// cannot leak into the persistence boundary or into a caller.
    /// </para>
    /// </summary>
    private const string ConversationScopeKind = "conversation";

    /// <summary>
    /// Scope kind of a conversation's coverage cursor: newer generations were verified and found
    /// unchanged, so no canonical publication happened.
    /// </summary>
    private const string ConversationCoverageScopeKind = "conversation_coverage";

    /// <summary>Scope kind of the account-wide generation scan cursor.</summary>
    private const string AccountScopeKind = "account";
    private readonly IRawVaultStore _rawVault = rawVault ?? throw new ArgumentNullException(nameof(rawVault));
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Reports committed ingest progress per account for freshness, interpreting the cursors this
    /// component owns. docs/HARNESS.md section 10, docs/DATA_MODEL.md section 23.3.
    /// <para>
    /// Conversation content progress and the account-wide generation scan are reported separately
    /// because they advance independently: a scoped ingest never advances the account scan, and a
    /// conversation whose newer evidence verified unchanged keeps its older content generation.
    /// A cursor this build cannot interpret contributes no generation rather than failing the whole
    /// status read — the row is still reported by time.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<IngestFreshness>> GetIngestFreshnessAsync(
        CancellationToken cancellationToken)
    {
        var checkpoints = await _archive.ListIngestCheckpointsAsync(cancellationToken)
            .ConfigureAwait(false);

        var byAccount = new Dictionary<string, IngestProgress>(StringComparer.Ordinal);
        foreach (var checkpoint in checkpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!byAccount.TryGetValue(checkpoint.AccountId, out var progress))
            {
                progress = new IngestProgress();
                byAccount[checkpoint.AccountId] = progress;
            }

            if (string.Equals(checkpoint.ScopeKind, ConversationScopeKind, StringComparison.Ordinal))
            {
                // Ties are broken by the stable scope id so the reported "latest" ingest is
                // deterministic even when two cursors share an update instant.
                var isNewer = progress.LastIngestAt is null
                    || checkpoint.UpdatedAt > progress.LastIngestAt
                    || (checkpoint.UpdatedAt == progress.LastIngestAt
                        && string.CompareOrdinal(
                            checkpoint.ScopeId,
                            progress.LatestIngestedConversationId ?? string.Empty) > 0);
                if (isNewer)
                {
                    progress.LastIngestAt = checkpoint.UpdatedAt;
                    progress.LatestIngestedConversationId = checkpoint.ScopeId;
                    progress.LatestIngestedGenerationId = TryReadContentGenerationId(checkpoint.CheckpointJson);
                }
            }
            else if (string.Equals(checkpoint.ScopeKind, AccountScopeKind, StringComparison.Ordinal)
                && (progress.LastAccountScanAt is null || checkpoint.UpdatedAt > progress.LastAccountScanAt))
            {
                progress.LastAccountScanAt = checkpoint.UpdatedAt;
            }
        }

        return
        [
            .. byAccount
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new IngestFreshness
                {
                    AccountId = pair.Key,
                    LastIngestAt = pair.Value.LastIngestAt,
                    LatestIngestedGenerationId = pair.Value.LatestIngestedGenerationId,
                    LatestIngestedConversationId = pair.Value.LatestIngestedConversationId,
                    LastAccountScanAt = pair.Value.LastAccountScanAt,
                }),
        ];
    }

    /// <summary>
    /// Reads the generation a conversation content cursor records, tolerantly: a payload this build
    /// cannot interpret contributes no generation instead of failing the status projection.
    /// <para>
    /// The cursor's own format version is checked first, so a future version that renames or nests
    /// this field degrades to "no generation reported" rather than being misread as if the field
    /// still meant the same thing. An account-scan cursor spells the field empty because it records
    /// covered generation identities separately, so an empty value means "no single generation"
    /// rather than a generation named <c>""</c>.
    /// </para>
    /// </summary>
    private static string? TryReadContentGenerationId(string checkpointJson)
    {
        const int SupportedCursorVersion = 1;

        try
        {
            using var document = JsonDocument.Parse(checkpointJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != SupportedCursorVersion
                || !root.TryGetProperty("generation_id", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var generationId = value.GetString();
            return string.IsNullOrWhiteSpace(generationId) ? null : generationId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class IngestProgress
    {
        public DateTimeOffset? LastIngestAt { get; set; }

        public string? LatestIngestedGenerationId { get; set; }

        public string? LatestIngestedConversationId { get; set; }

        public DateTimeOffset? LastAccountScanAt { get; set; }
    }

    /// <summary>
    /// Ingests one conversation selected by its stable conversation id or its upstream source
    /// conversation id. docs/ARCHITECTURE.md section 3.6.1.
    /// <para>
    /// Both selector forms are accepted so a multi-scope caller can consume the same stable
    /// identifier the discovery surface reports (docs/DATA_MODEL.md section 16), exactly as
    /// <c>conversation show</c> and <c>sync --conversation</c> already resolve it.
    /// </para>
    /// </summary>
    public Task<int> IngestConversationAsync(string accountId, string conversationSelector,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationSelector);
        return IngestAsync(accountId, conversationSelector, progress, cancellationToken);
    }

    /// <summary>Ingests one conversation from the exact generation published by live sync.</summary>
    public Task<int> IngestConversationFromGenerationAsync(string accountId, string conversationSelector,
        string generationId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationSelector);
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        return IngestAsync(accountId, conversationSelector, progress, cancellationToken,
            generationId: generationId);
    }

    /// <summary>
    /// Ingests Raw Vault evidence for one account.
    /// </summary>
    /// <param name="accountId">The stable account id whose vault is read.</param>
    /// <param name="sourceConversationId">
    /// <c>null</c> for an account-wide scan, otherwise one conversation selected by its stable
    /// conversation id (<c>g_…</c>/<c>u_…</c>) or its upstream source conversation id.
    /// </param>
    /// <param name="progress">Optional human progress.</param>
    /// <param name="cancellationToken">Cooperative cancellation; the in-flight conversation rolls back.</param>
    /// <param name="replay">Re-process preserved generations for parser repair instead of skipping covered ones.</param>
    public async Task<int> IngestAsync(string accountId, string? sourceConversationId,
        IProgress<string>? progress, CancellationToken cancellationToken, bool replay = false,
        string? generationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var generations = await _rawVault.ListGenerationsAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (generations.Count == 0)
            throw new InvalidOperationException("No published Raw Vault generations are available for this account.");
        var generationsById = generations.ToDictionary(generation => generation.GenerationId, StringComparer.Ordinal);
        if (generationId is not null)
        {
            var selectedGeneration = generations.FirstOrDefault(generation =>
                string.Equals(generation.GenerationId, generationId, StringComparison.Ordinal));
            if (selectedGeneration is null)
                throw new InvalidDataException($"Raw Vault generation '{generationId}' is not published for account '{accountId}'.");
            generations = [selectedGeneration];
        }

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
                .FirstOrDefault(conversation =>
                    string.Equals(conversation.SourceConversationId, sourceConversationId, StringComparison.Ordinal)
                    || string.Equals(conversation.Id, sourceConversationId, StringComparison.Ordinal));
        var knownSelectedConversation = selectedConversation is not null;
        var selectedCheckpoint = selectedConversation is null
            ? null
            : await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family, ConversationScopeKind,
                selectedConversation.Id, cancellationToken).ConfigureAwait(false);
        var selectedState = ReadCheckpoint(selectedCheckpoint?.CheckpointJson);
        var selectedCoverageCheckpoint = selectedConversation is null
            ? null
            : await _archive.GetIngestCheckpointAsync(accountId, WeChatCaptureAdapter.Family,
                ConversationCoverageScopeKind, selectedConversation.Id, cancellationToken).ConfigureAwait(false);
        var selectedCoverageState = ReadCheckpoint(selectedCoverageCheckpoint?.CheckpointJson);
        if (replay)
        {
            // A replay can publish an older generation before it reaches the current tip.
            // Invalidate unchanged-evidence cursors first so cancellation cannot leave a newer
            // coverage cursor in place and make a normal retry skip content that replay rolled back.
            var conversationsToInvalidate = sourceConversationId is null
                ? await _archive.ListConversationsAsync(accountId, cancellationToken).ConfigureAwait(false)
                : selectedConversation is null ? [] : [selectedConversation];
            foreach (var conversation in conversationsToInvalidate)
            {
                var invalidatedCoverage = CreateConversationCoverageCheckpoint(accountId,
                    WeChatCaptureAdapter.Family, conversation.Id, currentReaderVersion, "", "coverage_invalidated",
                    _clock.UtcNow);
                await CommitCoverageCheckpointAsync(conversation, invalidatedCoverage, cancellationToken).ConfigureAwait(false);
                if (selectedConversation?.Id == conversation.Id)
                    selectedCoverageState = ReadCheckpoint(invalidatedCoverage.CheckpointJson);
            }
        }
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
                if (sourceConversationId is not null && selectedCoverageState.ReaderVersion == currentReaderVersion
                    && IsAncestorOrSelf(generationsById, summary.GenerationId, selectedCoverageState.GenerationId))
                    continue;
            }

            // Covered history is skipped using its committed generation cursor. Content checkpoints
            // advance only when evidence changes; scoped scans use a separate transactional coverage
            // cursor for verified unchanged generations.
            var generation = await _rawVault.OpenGenerationAsync(accountId, summary.GenerationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Raw Vault generation '{summary.GenerationId}' failed manifest or checksum validation.");
            using var adapter = CapturedWeChatSourceAdapter.Create(generation);
            var account = (await adapter.ListAccountsAsync(cancellationToken).ConfigureAwait(false)).Single();
            if (!string.Equals(StableIds.Account(adapter.AdapterName, account.SourceProfileId), accountId, StringComparison.Ordinal))
                throw new InvalidDataException("Raw Vault account identity does not match the stable source identity.");
            IReadOnlyList<SourceConversation> sourceConversations;
            try
            {
                await RefreshParticipantsAsync(adapter, accountId, account.SourceProfileId, cancellationToken).ConfigureAwait(false);
                sourceConversations = await adapter.ListConversationsAsync(account.SourceProfileId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SourceCoverageException ex)
            {
                throw new InvalidDataException(
                    $"Raw Vault generation '{summary.GenerationId}' has incomplete coverage for account scope " +
                    $"'{accountId}': {ex.Message}", ex);
            }
            var importer = new ImportService(adapter, _archive, _clock);
            var conversations = sourceConversations;
            if (sourceConversationId is not null)
                conversations = conversations.Where(c => MatchesConversationSelector(accountId, c, sourceConversationId)).ToArray();

            var selectedFoundInGeneration = false;
            foreach (var conversation in conversations)
            {
                foundSelectedConversation = true;
                selectedFoundInGeneration = true;
                cancellationToken.ThrowIfCancellationRequested();
                var conversationId = StableIds.Conversation(accountId, conversation.Kind, conversation.SourceConversationId, conversation.PeerSourceUserId);
                var checkpoint = await _archive.GetIngestCheckpointAsync(accountId, generation.Manifest.Capture.CaptureAdapterFamily,
                    ConversationScopeKind, conversationId, cancellationToken).ConfigureAwait(false);
                var state = ReadCheckpoint(checkpoint?.CheckpointJson);
                if (!replay && state.ReaderVersion == adapter.AdapterVersion
                    && IsAncestorOrSelf(generationsById, summary.GenerationId, state.GenerationId))
                    continue;
                var coverageCheckpoint = await _archive.GetIngestCheckpointAsync(accountId,
                    generation.Manifest.Capture.CaptureAdapterFamily, ConversationCoverageScopeKind,
                    conversationId, cancellationToken).ConfigureAwait(false);
                var coverageState = ReadCheckpoint(coverageCheckpoint?.CheckpointJson);
                if (!replay && coverageState.ReaderVersion == adapter.AdapterVersion
                    && IsAncestorOrSelf(generationsById, summary.GenerationId, coverageState.GenerationId))
                    continue;
                SourceConversationDetail detail;
                string evidenceFingerprint;
                try
                {
                    detail = await adapter.DescribeConversationAsync(account.SourceProfileId,
                        conversation.SourceConversationId, cancellationToken).ConfigureAwait(false);
                    evidenceFingerprint = await FingerprintConversationAsync(adapter, account.SourceProfileId,
                        conversation, detail, cancellationToken).ConfigureAwait(false);
                }
                catch (SourceCoverageException ex)
                {
                    throw new InvalidDataException(
                        $"Raw Vault generation '{summary.GenerationId}' has incomplete coverage for conversation scope " +
                        $"'{conversation.SourceConversationId}': {ex.Message}", ex);
                }
                if (!replay && state.ReaderVersion == adapter.AdapterVersion
                    && string.Equals(state.Fingerprint, evidenceFingerprint, StringComparison.Ordinal))
                {
                    if (sourceConversationId is not null)
                    {
                        var existingConversation = await _archive.GetConversationAsync(conversationId, cancellationToken)
                            .ConfigureAwait(false)
                            ?? throw new InvalidDataException($"Conversation '{conversationId}' has a checkpoint but is absent from the archive.");
                        var coverage = CreateConversationCoverageCheckpoint(accountId,
                            generation.Manifest.Capture.CaptureAdapterFamily, conversationId, adapter.AdapterVersion,
                            summary.GenerationId, evidenceFingerprint, _clock.UtcNow);
                        await CommitCoverageCheckpointAsync(existingConversation, coverage, cancellationToken).ConfigureAwait(false);
                        selectedCoverageState = ReadCheckpoint(coverage.CheckpointJson);
                    }
                    continue;
                }

                var nextCheckpoint = new IngestCheckpoint
                {
                    Id = "ingest_" + Guid.NewGuid().ToString("N"),
                    AccountId = accountId,
                    AdapterFamily = generation.Manifest.Capture.CaptureAdapterFamily,
                    ScopeKind = ConversationScopeKind,
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
                }, progress is null ? null : new ImportStageProgress(progress, conversationId, summary.GenerationId),
                    cancellationToken).ConfigureAwait(false);
                if (outcome.Run.Status != ImportRunStatus.Completed)
                {
                    var fatal = outcome.Diagnostics.LastOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Fatal);
                    throw new InvalidDataException(
                        $"Ingest failed for Raw Vault generation '{summary.GenerationId}', conversation scope " +
                        $"'{conversation.SourceConversationId}' ({conversationId})" +
                        (fatal is null ? "." : $": {fatal.Message}"));
                }
                processed++;
                progress?.Report($"Ingested {conversationId} from generation {summary.GenerationId}");
            }

            if (sourceConversationId is not null && !selectedFoundInGeneration && selectedConversation is not null)
            {
                var coverage = CreateConversationCoverageCheckpoint(accountId,
                    generation.Manifest.Capture.CaptureAdapterFamily, selectedConversation.Id, adapter.AdapterVersion,
                    summary.GenerationId, selectedState.Fingerprint ?? "conversation_absent", _clock.UtcNow);
                await CommitCoverageCheckpointAsync(selectedConversation, coverage, cancellationToken).ConfigureAwait(false);
                selectedCoverageState = ReadCheckpoint(coverage.CheckpointJson);
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
            throw new ConversationNotInRawVaultException(sourceConversationId, accountId);
        return processed;
    }

    /// <summary>
    /// Matches a conversation against a caller's selector. Both the stable conversation id and the
    /// upstream source conversation id resolve the same conversation, mirroring
    /// <c>conversation show</c>/<c>sync --conversation</c> (docs/DATA_MODEL.md section 16).
    /// </summary>
    private static bool MatchesConversationSelector(string accountId, SourceConversation conversation, string selector) =>
        string.Equals(conversation.SourceConversationId, selector, StringComparison.Ordinal)
        || string.Equals(
            StableIds.Conversation(accountId, conversation.Kind, conversation.SourceConversationId, conversation.PeerSourceUserId),
            selector,
            StringComparison.Ordinal);

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

    private static IngestCheckpoint CreateConversationCoverageCheckpoint(string accountId, string adapterFamily,
        string conversationId, string readerVersion, string generationId, string evidenceFingerprint,
        DateTimeOffset updatedAt) => new()
    {
        Id = "ingest_" + Guid.NewGuid().ToString("N"),
        AccountId = accountId,
        AdapterFamily = adapterFamily,
        ScopeKind = ConversationCoverageScopeKind,
        ScopeId = conversationId,
        CheckpointJson = JsonSerializer.Serialize(new
        {
            version = 1,
            reader_version = readerVersion,
            generation_id = generationId,
            evidence_fingerprint = evidenceFingerprint,
        }),
        UpdatedAt = updatedAt,
    };

    private async Task CommitCoverageCheckpointAsync(ArchiveConversation conversation,
        IngestCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await using var session = await _archive.BeginConversationImportAsync(conversation, cancellationToken)
            .ConfigureAwait(false);
        await session.SetIngestCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RefreshParticipantsAsync(ISourceAdapter adapter, string accountId, string profileId,
        CancellationToken cancellationToken)
    {
        var descriptor = await adapter.DescribeSourceAsync(cancellationToken).ConfigureAwait(false);
        if (!descriptor.IsAvailable)
            throw new InvalidOperationException(descriptor.UnavailableReason ?? "The captured source is not available.");

        await _archive.UpsertAccountAsync(new ArchiveAccount
        {
            Id = accountId,
            SourceProfileId = profileId,
            AdapterName = adapter.AdapterName,
            AdapterVersion = adapter.AdapterVersion,
            SourceVersion = descriptor.SourceVersion,
            DisplayName = profileId,
        }, cancellationToken).ConfigureAwait(false);

        var participants = await adapter.ListParticipantsAsync(profileId, cancellationToken).ConfigureAwait(false);
        await _archive.UpsertParticipantsAsync(participants.Select(participant => new ArchiveParticipant
        {
            Id = StableIds.Participant(accountId, participant.SourceUserId),
            AccountId = accountId,
            SourceParticipantId = participant.SourceUserId,
            LatestRemark = participant.Remark,
            Nickname = participant.Nickname,
            Alias = participant.Alias,
        }), cancellationToken).ConfigureAwait(false);
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

    private sealed class ImportStageProgress(IProgress<string> target, string conversationId, string generationId)
        : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => target.Report(
            $"Ingesting {conversationId} from generation {generationId} ({value.Stage}: {value.Processed}/{value.Total})");
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
