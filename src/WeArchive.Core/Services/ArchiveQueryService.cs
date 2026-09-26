using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.RawVault;

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
/// Query is read-only (R0): no failure path mutates canonical data, Raw Vault evidence, the capture
/// checkpoint or an ingest checkpoint, and no persistent recovery state exists for it.
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
    private readonly IClock _clock;

    public ArchiveQueryService(IArchiveStore archive, IRawVaultStore rawVault, IClock clock)
    {
        _archive = archive ?? throw new ArgumentNullException(nameof(archive));
        _rawVault = rawVault ?? throw new ArgumentNullException(nameof(rawVault));
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
    /// Reports capture, ingest and canonical freshness together, so a caller can tell which stage
    /// is behind without reading a checkpoint table or a Raw Vault manifest
    /// (docs/HARNESS.md section 10).
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
            () => _archive.ListIngestFreshnessAsync(cancellationToken),
            cancellationToken).ConfigureAwait(false);

        var accountIds = await KnownAccountIdsAsync(accounts, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<string>> KnownAccountIdsAsync(
        IReadOnlyList<ArchiveAccount> accounts,
        CancellationToken cancellationToken)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var account in accounts)
            ids.Add(account.Id);

        // Captured evidence can exist before anything was ingested, and that gap is exactly what
        // freshness must show. The preservation store exposes account ids through its Core
        // contract, not through its file layout.
        try
        {
            foreach (var accountId in await _rawVault.ListAccountIdsAsync(cancellationToken).ConfigureAwait(false))
                ids.Add(accountId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // An unreadable preservation store is reported as "no capture generation known",
            // never as a fabricated generation and never as a failed query.
        }

        return [.. ids];
    }

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
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // There is no discoverable valid generation for this account; reporting nulls is the
            // honest answer and keeps the rest of the status readable.
            return new CaptureFreshness { AccountId = accountId };
        }
    }

    // ---- plumbing -----------------------------------------------------------

    private static MessageOrderKey OrderKeyOf(CanonicalMessage message) => new()
    {
        OccurredUtc = message.OccurredAt.ToUnixTimeSeconds(),
        SourceOrderKey = message.Source.SourceOrderKey ?? string.Empty,
        MessageId = message.Id,
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