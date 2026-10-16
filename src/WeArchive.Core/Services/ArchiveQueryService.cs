using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.RawVault;
using WeArchive.Core.Search;

namespace WeArchive.Core.Services;

/// <summary>
/// The source-independent retrieval boundary for canonical archive data.
/// docs/PRD.md FR-16/FR-17, docs/HARNESS.md sections 2–5, docs/ARCHITECTURE.md section 3.9.
/// <para>
/// CLI <c>--json</c> and a future MCP transport are adapters over this service; neither is allowed
/// to own query semantics. It reads only the canonical SQLite archive through
/// <see cref="IArchiveStore"/>, so listing messages needs no live WeChat client, no WeChat key and
/// no Raw Vault artifact, and never scans exported JSONL. Canonical DTOs and stable IDs cross this
/// boundary — never SQLite row shapes, WeChat tables or numeric source type codes.
/// </para>
/// <para>
/// Canonical full-text search is part of this boundary (M3b index slice,
/// docs/SEARCH_SPEC.md sections 5–9): the service owns query parsing, the CJK bigram projection of
/// every term, filter validation, Collection scope resolution and cursor handling; the archive
/// store owns the derived-index execution. Hits return the same canonical message DTO the M3a
/// commands return, and the result contract exposes no score — only the order.
/// </para>
/// <para>
/// Query is read-only (R0): no failure path mutates canonical data, Raw Vault evidence, the capture
/// checkpoint or an ingest checkpoint, and no persistent recovery state exists for it. A failing
/// derived index is diagnosed and repaired by explicit maintenance operations, never by a query.
/// </para>
/// <para>
/// The preservation store is used for exactly one thing: reporting capture freshness through the
/// source-neutral <see cref="IRawVaultStore"/> contract. Message retrieval never reads it, and no
/// Raw Vault physical layout reaches a caller (docs/PRD.md NFR-13).
/// </para>
/// </summary>
public sealed class ArchiveQueryService
{
    /// <summary>Page size used when a caller does not ask for one.</summary>
    public const int DefaultLimit = 100;

    /// <summary>Largest page a caller may request. Larger result sets stay cursor-paginated.</summary>
    public const int MaxLimit = 500;

    /// <summary>Context messages requested on each side of the target when unspecified.</summary>
    public const int DefaultContextMessages = 20;

    /// <summary>Largest context window a caller may request on each side of the target.</summary>
    public const int MaxContextMessages = 100;

    private readonly IArchiveStore _archive;
    private readonly IRawVaultStore _rawVault;
    private readonly IIngestProgressSource _ingestProgress;
    private readonly CollectionCatalogService _collections;
    private readonly IClock _clock;

    public ArchiveQueryService(
        IArchiveStore archive,
        IRawVaultStore rawVault,
        IIngestProgressSource ingestProgress,
        CollectionCatalogService collections,
        IClock clock)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _rawVault = rawVault ?? throw new ArgumentNullException(nameof(rawVault));
        _ingestProgress = ingestProgress ?? throw new ArgumentNullException(nameof(ingestProgress));
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Lists bounded canonical messages of one conversation in deterministic timeline order.
    /// </summary>
    /// <exception cref="ArchiveQueryException">
    /// The request is invalid, the cursor is unusable, the conversation is unknown or the archive
    /// cannot be read.
    /// </exception>
    public async Task<MessageQueryPage> ListMessagesAsync(
        MessageListRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ConversationId))
            throw ArchiveQueryException.Invalid("a stable conversation id is required.");

        var limit = ValidateLimit(request.Limit);
        var since = ParseInstant(request.Since, "since");
        var until = ParseInstant(request.Until, "until");
        if (since is not null && until is not null && since > until)
            throw ArchiveQueryException.Invalid("since must not be later than until.");

        var participant = ValidateParticipant(request.ParticipantId);
        var type = ParseType(request.Type);

        // The cursor is bound to the normalized filter set, so a caller can resume the same
        // listing with a different spelling of the same instant and still be accepted, while a
        // cursor from another query is rejected rather than silently paging the wrong timeline.
        var fingerprint = MessageCursor.Fingerprint(
            request.ConversationId, since, until, participant, type?.ToWireName());

        var query = new ArchiveMessageQuery
        {
            ConversationId = request.ConversationId,
            Since = since,
            Until = until,
            ParticipantId = participant,
            Type = type,
            Limit = limit,
            After = request.Cursor is null ? null : MessageCursor.Decode(fingerprint, request.Cursor),
        };

        // An unknown conversation is a deterministic product outcome rather than an empty listing,
        // so a caller can tell "this conversation has no matching messages" from "wrong id".
        var conversation = await ReadAsync(
            () => _archive.GetConversationAsync(request.ConversationId, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (conversation is null)
            throw ArchiveQueryException.ConversationNotFound(request.ConversationId);

        var page = await ReadAsync(
            () => _archive.QueryMessagesAsync(query, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // A page that reports more records must hand back the cursor that reaches them; otherwise
        // the caller would be told to continue with no way to continue.
        var hasMore = page.HasMore && page.Items.Count > 0;
        var nextCursor = hasMore
            ? MessageCursor.Encode(fingerprint, OrderKeyOf(page.Items[^1]))
            : null;

        return new MessageQueryPage
        {
            Items = page.Items,
            HasMore = hasMore,
            NextCursor = nextCursor,
        };
    }

    /// <summary>
    /// Returns the bounded canonical window around one stable message ID.
    /// </summary>
    /// <exception cref="ArchiveQueryException">
    /// A bound is invalid, the message is unknown or the archive cannot be read.
    /// </exception>
    public async Task<MessageContextWindow> GetContextAsync(
        string messageId,
        int before,
        int after,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            throw ArchiveQueryException.Invalid("a stable message id is required.");

        ValidateContextCount(before, "before");
        ValidateContextCount(after, "after");

        var context = await ReadAsync(
            () => _archive.ReadMessageContextAsync(messageId, before, after, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (context is null)
            throw ArchiveQueryException.MessageNotFound(messageId);

        return new MessageContextWindow
        {
            Message = context.Target,
            Before = context.Before,
            After = context.After,
        };
    }

    /// <summary>
    /// Runs one bounded page of a canonical full-text search (M3b index slice,
    /// docs/SEARCH_SPEC.md sections 5–9).
    /// </summary>
    /// <exception cref="ArchiveQueryException">
    /// The query is invalid (<c>query_invalid</c>), a filter or cursor is unusable, the requested
    /// conversation or Collection is unknown, or the derived index is missing or inconsistent
    /// (<c>search_index_unavailable</c> — fail-closed, never repaired by a query).
    /// </exception>
    public async Task<SearchPage> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The query is validated and compiled before any archive access: a deterministic usage
        // failure must not depend on archive state, and no invalid query ever reaches FTS5
        // (docs/SEARCH_SPEC.md section 5.3).
        var terms = SearchQueryCompiler.Parse(request.Query);
        var matchExpression = SearchQueryCompiler.Compile(terms);

        var limit = ValidateLimit(request.Limit);
        var since = ParseInstant(request.Since, "since");
        var until = ParseInstant(request.Until, "until");
        if (since is not null && until is not null && since > until)
            throw ArchiveQueryException.Invalid("since must not be later than until.");

        var participant = ValidateParticipant(request.ParticipantId);
        var type = ParseType(request.Type);

        // --conversation and --collection are mutually exclusive scopes in v1
        // (docs/SEARCH_SPEC.md section 7).
        if (request.ConversationId is not null && request.CollectionName is not null)
            throw ArchiveQueryException.Invalid(
                "conversation and collection scopes are mutually exclusive; supply one search scope.");

        string? conversationId = null;
        IReadOnlyList<string> collectionConversationIds = [];
        if (request.ConversationId is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ConversationId))
                throw ArchiveQueryException.Invalid("a stable conversation id is required.");

            conversationId = request.ConversationId;
            var conversation = await ReadAsync(
                () => _archive.GetConversationAsync(conversationId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (conversation is null)
                throw ArchiveQueryException.ConversationNotFound(conversationId);
        }
        else if (request.CollectionName is not null)
        {
            if (string.IsNullOrWhiteSpace(request.CollectionName))
                throw ArchiveQueryException.Invalid("a collection name is required.");

            // The one authoritative Collection catalog resolves the scope; an unknown name and an
            // invalid configuration are deterministic failures, while a member that resolves to no
            // archived conversation contributes no results and never errors by itself.
            CollectionDefinition definition;
            try
            {
                definition = await _collections.ResolveAsync(request.CollectionName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CollectionNotFoundException)
            {
                throw ArchiveQueryException.CollectionNotFound(request.CollectionName);
            }
            catch (CollectionConfigurationException exception)
            {
                throw ArchiveQueryException.CollectionConfigInvalid(exception);
            }

            collectionConversationIds = definition.ConversationIds;
        }

        // The cursor is bound to the normalized query (the parsed term structure, so two spellings
        // of the same search share a cursor) plus the resolved filter set. The page size is
        // excluded, so a caller may change the limit between pages (docs/SEARCH_SPEC.md section 8).
        var fingerprint = SearchCursor.Fingerprint(
            terms,
            conversationId,
            collectionConversationIds,
            since?.ToUnixTimeSeconds(),
            until?.ToUnixTimeSeconds(),
            participant,
            type?.ToWireName());

        var page = await ReadAsync(
            () => _archive.SearchMessagesAsync(new ArchiveSearchQuery
            {
                MatchExpression = matchExpression,
                ConversationId = conversationId,
                ConversationIds = request.CollectionName is null ? null : collectionConversationIds,
                Since = since,
                Until = until,
                ParticipantId = participant,
                Type = type,
                Limit = limit,
                After = request.Cursor is null ? null : SearchCursor.Decode(fingerprint, request.Cursor),
            }, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // A page that reports more rows must hand back the cursor that reaches them; otherwise the
        // caller would be told to continue with no way to continue. Zero matches is a successful
        // result with no cursor (docs/SEARCH_SPEC.md section 5.4).
        var hasMore = page.HasMore && page.Hits.Count > 0;
        var nextCursor = hasMore
            ? SearchCursor.Encode(fingerprint, OrderKeyOf(page.Hits[^1]))
            : null;

        return new SearchPage
        {
            Items = [.. page.Hits.Select(hit => hit.Message)],
            HasMore = hasMore,
            NextCursor = nextCursor,
        };
    }

    /// <summary>
    /// Computes one bounded, read-only statistics report over the canonical archive (M3c,
    /// docs/DATA_MODEL.md section 24, docs/PRD.md FR-16/FR-17).
    /// <para>
    /// The service owns scope resolution (one conversation, one Collection's archived membership,
    /// or the whole archive), filter validation and the opaque breakdown cursor; the archive store
    /// derives every count from canonical rows at query time. No derived statistics state is read
    /// or written, nothing touches the Raw Vault, and unknown, partial and unattributed records
    /// are counted wherever they fall instead of being silently dropped.
    /// </para>
    /// </summary>
    /// <exception cref="ArchiveQueryException">
    /// The request is invalid, the cursor is unusable, the requested conversation or Collection is
    /// unknown, or the archive cannot be read.
    /// </exception>
    public async Task<ArchiveStatistics> GetStatisticsAsync(
        StatisticsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var breakdown = ParseBreakdown(request.Bucket);
        var limit = ValidateLimit(request.Limit);
        var since = ParseInstant(request.Since, "since");
        var until = ParseInstant(request.Until, "until");
        if (since is not null && until is not null && since > until)
            throw ArchiveQueryException.Invalid("since must not be later than until.");

        // --conversation and --collection are mutually exclusive scopes, exactly as search scopes
        // them: one report counts one resolved scope, never an ambiguous union.
        if (request.ConversationId is not null && request.CollectionName is not null)
            throw ArchiveQueryException.Invalid(
                "conversation and collection scopes are mutually exclusive; supply one statistics scope.");

        string? conversationId = null;
        IReadOnlyList<string>? collectionConversationIds = null;
        var scopeKind = "archive";
        if (request.ConversationId is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ConversationId))
                throw ArchiveQueryException.Invalid("a stable conversation id is required.");

            scopeKind = "conversation";
            conversationId = request.ConversationId;
            var conversation = await ReadAsync(
                () => _archive.GetConversationAsync(conversationId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (conversation is null)
                throw ArchiveQueryException.ConversationNotFound(conversationId);
        }
        else if (request.CollectionName is not null)
        {
            if (string.IsNullOrWhiteSpace(request.CollectionName))
                throw ArchiveQueryException.Invalid("a collection name is required.");

            // The one authoritative Collection catalog resolves the scope; an unknown name and an
            // invalid configuration are deterministic failures, while a member that resolves to no
            // archived conversation contributes no counts and never errors by itself.
            CollectionDefinition definition;
            try
            {
                definition = await _collections.ResolveAsync(request.CollectionName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CollectionNotFoundException)
            {
                throw ArchiveQueryException.CollectionNotFound(request.CollectionName);
            }
            catch (CollectionConfigurationException exception)
            {
                throw ArchiveQueryException.CollectionConfigInvalid(exception);
            }

            scopeKind = "collection";
            collectionConversationIds = definition.ConversationIds;
        }

        // The cursor is bound to the normalized scope/filter set (the resolved member IDs, so a
        // catalog change cannot silently repoint a page) plus the breakdown kind. The page size is
        // excluded, so a caller may change the limit between pages.
        var fingerprint = StatisticsCursor.Fingerprint(
            scopeKind,
            conversationId,
            collectionConversationIds,
            since,
            until,
            breakdown.ToWireName());

        string? after = null;
        if (request.Cursor is not null)
        {
            after = StatisticsCursor.RequirePosition(
                breakdown,
                StatisticsCursor.Decode(fingerprint, request.Cursor));
        }

        var result = await ReadAsync(
            () => _archive.GetArchiveStatisticsAsync(new ArchiveStatisticsQuery
            {
                ConversationIds = conversationId is not null
                    ? [conversationId]
                    : collectionConversationIds,
                Since = since,
                Until = until,
                Breakdown = breakdown,
                BreakdownAfter = after,
                BreakdownLimit = limit,
            }, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        // A page that reports more buckets must hand back the cursor that reaches them; otherwise
        // the caller would be told to continue with no way to continue.
        var hasMore = result.BreakdownHasMore && result.BreakdownItems.Count > 0;
        var nextCursor = hasMore
            ? StatisticsCursor.Encode(fingerprint, StatisticsCursor.PositionOf(breakdown, result.BreakdownItems[^1]))
            : null;

        return new ArchiveStatistics
        {
            ScopeKind = scopeKind,
            ConversationId = conversationId,
            CollectionName = request.CollectionName,
            Since = since,
            Until = until,
            ArchivePath = _archive.ArchivePath,
            QueriedAt = _clock.UtcNow,
            Totals = result.Totals,
            ByType = result.ByType,
            Breakdown = breakdown,
            BreakdownItems = result.BreakdownItems,
            HasMore = hasMore,
            NextCursor = nextCursor,
        };
    }

    /// <summary>
    /// Reports capture, ingest and canonical freshness together, so a caller can tell which stage
    /// is behind without reading a checkpoint table or a Raw Vault manifest
    /// (docs/HARNESS.md section 10).
    /// <para>
    /// Canonical status is always reported. A preservation store that cannot be read degrades the
    /// capture half into an explicit reason instead of failing the whole read, because a status
    /// projection that disappears when one stage is unreadable is less useful than one that says
    /// which stage is unreadable.
    /// </para>
    /// </summary>
    public async Task<ArchiveFreshness> GetFreshnessAsync(CancellationToken cancellationToken)
    {
        var stats = await ReadAsync(
            () => _archive.GetArchiveStatsAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var accounts = await ReadAsync(
            () => _archive.ListAccountsAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var ingest = await ReadAsync(
            () => _ingestProgress.GetIngestFreshnessAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var (accountIds, captureUnavailableReason) =
            await KnownAccountIdsAsync(accounts, cancellationToken).ConfigureAwait(false);
        var ingestByAccount = ingest.ToDictionary(row => row.AccountId, StringComparer.Ordinal);

        var capture = new List<CaptureFreshness>(accountIds.Count);
        var ingestRows = new List<IngestFreshness>(accountIds.Count);
        foreach (var accountId in accountIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture.Add(await ReadCaptureFreshnessAsync(accountId, cancellationToken).ConfigureAwait(false));

            // Every known account gets an ingest row, even when nothing was ingested yet: that
            // null is precisely the "captured but not ingested" state freshness must show.
            ingestRows.Add(ingestByAccount.TryGetValue(accountId, out var row)
                ? row
                : new IngestFreshness { AccountId = accountId });
        }

        return new ArchiveFreshness
        {
            Canonical = new CanonicalFreshness
            {
                ArchivePath = stats.ArchivePath,
                AccountCount = stats.AccountCount,
                ConversationCount = stats.ConversationCount,
                ParticipantCount = stats.ParticipantCount,
                MessageCount = stats.MessageCount,
                LastMessageAt = stats.LastMessageAt,
                QueriedAt = _clock.UtcNow,
            },
            CaptureUnavailableReason = captureUnavailableReason,
            Capture = capture,
            Ingest = ingestRows,
        };
    }

    // ---- request validation -------------------------------------------------

    private static int ValidateLimit(int? limit)
    {
        var value = limit ?? DefaultLimit;
        if (value < 1 || value > MaxLimit)
            throw ArchiveQueryException.Invalid($"limit must be between 1 and {MaxLimit}.");

        return value;
    }

    /// <summary>
    /// Parses one optional date/date-time bound in the machine's local offset when the caller did
    /// not state one (docs/CLI.md). An unspecified bound stays null; an unparseable one is rejected
    /// rather than silently ignored, so a typo can never widen a query.
    /// </summary>
    private DateTimeOffset? ParseInstant(string? text, string option)
    {
        if (text is null)
            return null;

        if (!FilterValues.TryParseInstant(text, _clock.LocalOffset, out var value, out var error))
            throw ArchiveQueryException.Invalid($"{option}: {error}");

        return value;
    }

    private static CanonicalMessageType? ParseType(string? type)
    {
        if (type is null)
            return null;

        if (type.Length == 0 || !CanonicalMessageTypes.TryParse(type, out var parsed))
            throw ArchiveQueryException.Invalid("type must be a canonical message type wire name.");

        return parsed;
    }

    /// <summary>
    /// Parses one optional breakdown wire name. An unspecified breakdown is the default day
    /// timeline; an unsupported value is a usage failure rather than a silently different
    /// bucketing (docs/CLI.md <c>wearchive stats</c>).
    /// </summary>
    private static StatisticsBreakdownKind ParseBreakdown(string? bucket)
    {
        if (bucket is null)
            return StatisticsBreakdownKind.Day;

        if (bucket.Length == 0 || !StatisticsBreakdownKinds.TryParse(bucket, out var parsed))
            throw ArchiveQueryException.Invalid("bucket must be one of: day, month, conversation.");

        return parsed;
    }

    /// <summary>
    /// A participant filter must address the same stable identity the archive stores, so a display
    /// name, a conversation id or a truncated id fails deterministically instead of matching nothing.
    /// </summary>
    private static string? ValidateParticipant(string? participantId)
    {
        if (participantId is null)
            return null;

        if (!IsCanonicalParticipantId(participantId))
        {
            throw ArchiveQueryException.Invalid(
                "participant must be a canonical stable participant id (u_<16 hex>).");
        }

        return participantId;
    }

    private static void ValidateContextCount(int value, string option)
    {
        if (value < 0 || value > MaxContextMessages)
            throw ArchiveQueryException.Invalid($"{option} must be between 0 and {MaxContextMessages}.");
    }

    private static bool IsCanonicalParticipantId(string value)
    {
        if (value.Length != 18 || value[0] != 'u' || value[1] != '_')
            return false;

        for (var i = 2; i < value.Length; i++)
        {
            var c = value[i];
            var isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowerHex)
                return false;
        }

        return true;
    }

    // ---- freshness helpers --------------------------------------------------

    private async Task<(IReadOnlyList<string> AccountIds, string? UnavailableReason)> KnownAccountIdsAsync(
        IReadOnlyList<ArchiveAccount> accounts,
        CancellationToken cancellationToken)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
            ids.Add(account.Id);

        // Captured evidence can exist before anything was ingested, and that gap is exactly what
        // freshness must show. When the preservation store cannot be enumerated at all, the
        // canonical accounts are still reported and the failure is returned as an explicit reason,
        // so a vault-only account is visibly unknown instead of silently missing.
        try
        {
            foreach (var accountId in await _rawVault.ListAccountIdsAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(accountId);

            return ([.. ids], null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ([.. ids], Describe(ex));
        }
    }

    /// <summary>
    /// The latest published generation of one account, or an explicit reason why none can be
    /// reported. Any non-cancellation failure degrades: the preservation store's failure vocabulary
    /// is not this projection's contract, and a status read must not disappear because one stage of
    /// it is unreadable.
    /// </summary>
    private async Task<CaptureFreshness> ReadCaptureFreshnessAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        try
        {
            var latest = await _rawVault.GetLatestGenerationAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            if (latest is null)
                return new CaptureFreshness { AccountId = accountId };

            return new CaptureFreshness
            {
                AccountId = accountId,
                GenerationId = latest.GenerationId,
                CaptureTime = latest.CaptureTime,
                Completeness = latest.Completeness,
                ArtifactCount = latest.ArtifactCount,
                PreviousGenerationId = latest.PreviousGenerationId,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CaptureFreshness { AccountId = accountId, UnavailableReason = Describe(ex) };
        }
    }

    /// <summary>
    /// A short engineering reason for a stage that could not be read. It is an exception type and
    /// message only, so a real defect stays diagnosable instead of merely presenting as an
    /// unavailable archive.
    /// </summary>
    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    // ---- plumbing -----------------------------------------------------------

    private static MessageOrderKey OrderKeyOf(CanonicalMessage message) => new()
    {
        OccurredUtc = message.OccurredAt.ToUnixTimeSeconds(),
        SourceOrderKey = message.Source.SourceOrderKey ?? string.Empty,
        MessageId = message.Id,
    };

    /// <summary>
    /// The exclusive search-order position after the page's last hit: bm25 rank first, then the
    /// canonical timeline tail (docs/SEARCH_SPEC.md section 6). The rank is an ordering input for
    /// the next page's keyset and never becomes a result value.
    /// </summary>
    private static SearchOrderPosition OrderKeyOf(ArchiveSearchHit hit) => new()
    {
        Rank = hit.Rank,
        OccurredUtc = hit.Message.OccurredAt.ToUnixTimeSeconds(),
        SourceOrderKey = hit.Message.Source.SourceOrderKey ?? string.Empty,
        MessageId = hit.Message.Id,
    };

    /// <summary>
    /// Runs one archive read: cancellation stays cancellation, a documented query failure stays
    /// itself, and any other failure is an unavailable archive rather than an invented result.
    /// </summary>
    private static async Task<T> ReadAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveQueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw ArchiveQueryException.Unavailable(ex);
        }
    }
}