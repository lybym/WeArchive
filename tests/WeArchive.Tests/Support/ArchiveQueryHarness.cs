using System.Text.Json.Nodes;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;

namespace WeArchive.Tests.Support;

/// <summary>
/// A synthetic canonical archive for the M3a query tests (docs/DEVELOPMENT.md section 7).
/// <para>
/// It is deliberately built through the real <see cref="SqliteArchiveStore"/> write path rather
/// than by inserting rows, so the tests exercise the shipped timeline index, column order and
/// upsert semantics. The fixture covers the cases the Issue calls out: several conversations,
/// several senders, several canonical types, identical timestamps with different (and missing)
/// source order keys, an <c>unknown</c> record, a partial record, a resolved reply and a
/// provenance-only reply snapshot.
/// </para>
/// </summary>
internal sealed class ArchiveQueryHarness : IDisposable
{
    public const string AccountId = "a_00000000000000a1";
    public const string GroupConversationId = "g_00000000000000c1";
    public const string SecondConversationId = "g_00000000000000c2";
    public const string AliceId = "u_00000000000000a1";
    public const string BobId = "u_00000000000000b2";
    public const string GroupSourceId = "100200300@chatroom";
    public const string SecondSourceId = "400500600@chatroom";

    /// <summary>Stable IDs of the group fixture messages, in insertion order.</summary>
    public const string FirstId = "m_0000000000000001";
    public const string SecondId = "m_0000000000000002";
    public const string UnknownId = "m_0000000000000003";
    public const string ImageId = "m_0000000000000004";
    public const string PartialId = "m_0000000000000005";
    public const string SystemId = "m_0000000000000006";
    public const string EqualTimestampFirstId = "m_0000000000000007";
    public const string EqualTimestampSecondId = "m_0000000000000008";
    public const string NullOrderKeyId = "m_0000000000000009";
    public const string OtherConversationId = "m_0000000000000101";

    private readonly TempDirectory _temp = new();

    public ArchiveQueryHarness()
    {
        Clock = new FixedClock();
        ArchivePath = _temp.Combine("archive", "wearchive.db");
        Store = new SqliteArchiveStore(ArchivePath, Clock);
        Vault = new FakeRawVaultStore();
        Service = new ArchiveQueryService(Store, Vault, Clock);
    }

    public FixedClock Clock { get; }

    public string ArchivePath { get; }

    public SqliteArchiveStore Store { get; }

    public FakeRawVaultStore Vault { get; }

    public ArchiveQueryService Service { get; }

    public string VaultRoot => _temp.Combine("vault");

    /// <summary>Every seeded group-conversation message, in the order they were written.</summary>
    public IReadOnlyList<CanonicalMessage> GroupMessages { get; private set; } = [];

    /// <summary>Every seeded second-conversation message.</summary>
    public IReadOnlyList<CanonicalMessage> OtherMessages { get; private set; } = [];

    /// <summary>An instant in the fixture timezone (+08:00), which is also the fixed clock offset.</summary>
    public static DateTimeOffset At(int month, int day, int hour = 9, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(8));

    /// <summary>
    /// Canonical timeline order as docs/DATA_MODEL.md section 8.1 defines it:
    /// <c>(occurred_utc, source_order_key, id)</c> with a missing order key treated as empty.
    /// It is the expectation the archive's own ordering is compared against.
    /// </summary>
    public static IReadOnlyList<CanonicalMessage> CanonicalOrder(IEnumerable<CanonicalMessage> messages) =>
    [
        .. messages
            .OrderBy(message => message.OccurredAt.ToUnixTimeSeconds())
            .ThenBy(message => message.Source.SourceOrderKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(message => message.Id, StringComparer.Ordinal),
    ];

    public async Task SeedAsync()
    {
        await Store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = AccountId,
            SourceProfileId = "wxid_fixture",
            AdapterName = "wechat-windows",
            AdapterVersion = "0.1.0",
            SourceVersion = "4.1.13.12",
        }, CancellationToken.None).ConfigureAwait(false);

        await Store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = GroupConversationId,
                AccountId = AccountId,
                SourceConversationId = GroupSourceId,
                Kind = ConversationKind.Group,
                Title = "Alpha",
            },
            new ArchiveConversation
            {
                Id = SecondConversationId,
                AccountId = AccountId,
                SourceConversationId = SecondSourceId,
                Kind = ConversationKind.Group,
                Title = "Beta",
            },
        ], CancellationToken.None).ConfigureAwait(false);

        GroupMessages =
        [
            Message(FirstId, GroupConversationId, At(1, 1), AliceId, CanonicalMessageType.Text, "morning", "0001", "l:message_0:1"),
            Message(
                SecondId,
                GroupConversationId,
                At(1, 1),
                BobId,
                CanonicalMessageType.Text,
                "morning",
                "0002",
                "l:message_0:2",
                replyToSourceId: "l:message_0:1"),
            // Unknown semantics survive as first-class retained records (docs/DATA_MODEL.md section 12).
            Message(UnknownId, GroupConversationId, At(1, 1), AliceId, CanonicalMessageType.Unknown, "[未识别消息]", "0003", "l:message_0:3"),
            Message(
                ImageId,
                GroupConversationId,
                At(1, 2),
                BobId,
                CanonicalMessageType.Image,
                "[图片]",
                "0001",
                "l:message_0:4",
                payload: new JsonObject { ["file_name"] = "chart.png" }),
            Message(PartialId, GroupConversationId, At(1, 3), AliceId, CanonicalMessageType.Text, "partial body", "0001", "l:message_0:5", isPartial: true),
            // A record whose sender could not be resolved must stay visible with a null sender.
            Message(SystemId, GroupConversationId, At(1, 4), senderId: null, CanonicalMessageType.System, "系统消息", "0001", "l:message_0:6"),
            Message(EqualTimestampFirstId, GroupConversationId, At(1, 5), AliceId, CanonicalMessageType.Link, "[链接] title", "0001", "l:message_0:7"),
            // Identical instant and identical order key: only the stable message id separates them.
            Message(EqualTimestampSecondId, GroupConversationId, At(1, 5), BobId, CanonicalMessageType.Text, "tie", "0001", "l:message_0:8"),
            // No upstream order key at all: the timeline still places it deterministically.
            Message(NullOrderKeyId, GroupConversationId, At(1, 5), AliceId, CanonicalMessageType.Text, "no order key", null, "l:message_0:9"),
        ];

        OtherMessages =
        [
            Message(OtherConversationId, SecondConversationId, At(2, 1), AliceId, CanonicalMessageType.Text, "other conversation", "0001", "l:message_1:1"),
            Message("m_0000000000000102", SecondConversationId, At(2, 1, 10), BobId, CanonicalMessageType.File, "[文件] report.pdf", "0001", "l:message_1:2"),
            Message("m_0000000000000103", SecondConversationId, At(2, 2), AliceId, CanonicalMessageType.Voice, "[语音]", "0001", "l:message_1:3"),
        ];

        await Store.UpsertMessagesAsync(GroupMessages, CancellationToken.None).ConfigureAwait(false);
        await Store.UpsertMessagesAsync(OtherMessages, CancellationToken.None).ConfigureAwait(false);

        // Resolve the reply relationship the same way a real import does, so the query DTO exposes
        // a resolved canonical target rather than an unresolved snapshot.
        await Store.ResolveReplyTargetsAsync(GroupConversationId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Reads the group conversation back through the unbounded read path for comparison.</summary>
    public async Task<IReadOnlyList<CanonicalMessage>> ReadGroupMessagesAsync() =>
        await Store.ReadMessagesAsync(GroupConversationId, CancellationToken.None).ConfigureAwait(false);

    public void Dispose() => _temp.Dispose();

    private static CanonicalMessage Message(
        string id,
        string conversationId,
        DateTimeOffset occurredAt,
        string? senderId,
        CanonicalMessageType type,
        string text,
        string? orderKey,
        string sourceMessageId,
        JsonObject? payload = null,
        bool isPartial = false,
        string? replyToSourceId = null) => new()
        {
            Id = id,
            ConversationId = conversationId,
            SenderId = senderId,
            OccurredAt = occurredAt,
            Type = type,
            Text = text,
            Payload = payload,
            IsPartial = isPartial,
            ReplyTo = replyToSourceId is null
                ? null
                : new ReplyReference
                {
                    SourceMessageId = replyToSourceId,
                    SenderId = AliceId,
                    SenderName = "Alice",
                    Text = "morning",
                    OccurredAt = At(1, 1),
                },
            Source = new SourceProvenance
            {
                SourceProfileId = "wxid_fixture",
                SourceConversationId = conversationId == GroupConversationId ? GroupSourceId : SecondSourceId,
                SourceMessageId = sourceMessageId,
                SourceType = "1",
                SourcePartition = "message_0",
                SourceOrderKey = orderKey,
                AdapterName = "wechat-windows",
                AdapterVersion = "0.1.0",
                SourceVersion = "4.1.13.12",
            },
        };
}

/// <summary>
/// Preservation-store stand-in whose capture state the test controls directly, so freshness
/// projection can be asserted without publishing real Raw Vault generations.
/// </summary>
internal sealed class FakeRawVaultStore : IRawVaultStore
{
    public string VaultRoot => "<fake-vault>";

    public IReadOnlyList<string> AccountIds { get; set; } = [];

    public Dictionary<string, RawGenerationSummary> Latest { get; } = new(StringComparer.Ordinal);

    public List<string> Calls { get; } = [];

    /// <summary>
    /// When set, the store behaves like an unreadable preservation store, so freshness reporting
    /// can be asserted for the "cannot report a generation" case.
    /// </summary>
    public Exception? Failure { get; set; }

    public Task<IReadOnlyList<string>> ListAccountIdsAsync(CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ListAccountIdsAsync));
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
            throw Failure;

        return Task.FromResult<IReadOnlyList<string>>([.. AccountIds.OrderBy(id => id, StringComparer.Ordinal)]);
    }

    public Task<IReadOnlyList<RawGenerationSummary>> ListGenerationsAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("QueryService must not enumerate every generation.");

    public Task<RawGenerationSummary?> GetLatestGenerationAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetLatestGenerationAsync));
        cancellationToken.ThrowIfCancellationRequested();
        if (Failure is not null)
            throw Failure;

        return Task.FromResult(Latest.TryGetValue(accountId, out var summary) ? summary : null);
    }

    public Task<IRawGenerationSession> BeginGenerationAsync(
        RawGenerationContext context,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("QueryService is read-only.");

    public Task<RawGeneration?> OpenGenerationAsync(
        string accountId,
        string generationId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("QueryService must not open Raw Vault artifacts.");
}

/// <summary>
/// Preservation store that fails on every call. Query tests use it to prove that canonical
/// retrieval reads no Raw Vault state at all (docs/PRD.md NFR-13, docs/HARNESS.md section 2).
/// </summary>
internal sealed class ThrowingRawVaultStore : IRawVaultStore
{
    private static InvalidOperationException Failure() =>
        new("the canonical query path must not read the Raw Vault.");

    public string VaultRoot => throw Failure();

    public Task<IReadOnlyList<string>> ListAccountIdsAsync(CancellationToken cancellationToken) =>
        throw Failure();

    public Task<IReadOnlyList<RawGenerationSummary>> ListGenerationsAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        throw Failure();

    public Task<RawGenerationSummary?> GetLatestGenerationAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        throw Failure();

    public Task<IRawGenerationSession> BeginGenerationAsync(
        RawGenerationContext context,
        CancellationToken cancellationToken) =>
        throw Failure();

    public Task<RawGeneration?> OpenGenerationAsync(
        string accountId,
        string generationId,
        CancellationToken cancellationToken) =>
        throw Failure();
}

/// <summary>
/// Source adapter that fails on every call. Query tests use it to prove that canonical retrieval
/// needs no live WeChat client (docs/PRD.md NFR-06).
/// </summary>
internal sealed class ThrowingSourceAdapter : ISourceAdapter
{
    private static InvalidOperationException Failure() =>
        new("the canonical query path must not contact the live source.");

    public string AdapterName => "throwing";

    public string AdapterVersion => "0.0.0";

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
        throw Failure();

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
        throw Failure();

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        throw Failure();

    public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        throw Failure();

    public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        throw Failure();

    public Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        throw Failure();
}