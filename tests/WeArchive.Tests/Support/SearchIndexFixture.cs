using System.Text.Json.Nodes;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;

namespace WeArchive.Tests.Support;

/// <summary>
/// Seeds a real canonical archive with the deterministic synthetic corpus of
/// <see cref="SearchBenchmarkCorpus"/>, shaped exactly as the archive would hold it
/// (docs/DEVELOPMENT.md section 7 fixture strategy).
/// <para>
/// Every corpus document becomes one canonical message whose stable id is the document id, so
/// probe ground truth (seed document ids) can be compared directly with retrieved message ids.
/// Payload fields are stored under the canonical payload keys of docs/MESSAGE_SCHEMA.md and every
/// message carries full provenance. Nothing here is real conversation data.
/// </para>
/// </summary>
internal static class SearchIndexFixture
{
    public const string AccountId = "a_00000000000000c1";

    public const string DirectConversationId = "g_0000000000000b01";
    public const string GroupConversationId = "g_0000000000000b02";
    public const string OfficialConversationId = "u_0000000000000b03";

    public const string AliceId = "u_00000000000000a1";
    public const string BobId = "u_00000000000000b2";

    /// <summary>Corpus conversation bucket → canonical stable conversation id.</summary>
    public static readonly IReadOnlyDictionary<string, string> ConversationIds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SearchBenchmarkCorpus.DirectConversation] = DirectConversationId,
            [SearchBenchmarkCorpus.GroupConversation] = GroupConversationId,
            [SearchBenchmarkCorpus.OfficialConversation] = OfficialConversationId,
        };

    /// <summary>An instant in the fixture timezone (+08:00), deterministic per document ordinal.</summary>
    public static DateTimeOffset At(int ordinal) =>
        new DateTimeOffset(2026, 1, 1, 9, ordinal % 60, 0, TimeSpan.FromHours(8)).AddDays(ordinal / 60);

    /// <summary>
    /// Creates the archive schema and seeds the account, the corpus conversations and one canonical
    /// message per corpus document, through the real store write path.
    /// </summary>
    public static async Task<SqliteArchiveStore> CreateSeededArchiveAsync(string archivePath)
    {
        var store = new SqliteArchiveStore(archivePath, new FixedClock());
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = AccountId,
            SourceProfileId = "wxid_search_fixture",
            AdapterName = "wechat-windows",
            AdapterVersion = "0.1.0",
            SourceVersion = "4.1.13.12",
        }, CancellationToken.None).ConfigureAwait(false);
        await store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = DirectConversationId, AccountId = AccountId,
                SourceConversationId = "1000001@chatroom", Kind = ConversationKind.Group, Title = "Direct bucket",
            },
            new ArchiveConversation
            {
                Id = GroupConversationId, AccountId = AccountId,
                SourceConversationId = "1000002@chatroom", Kind = ConversationKind.Group, Title = "Group bucket",
            },
            new ArchiveConversation
            {
                Id = OfficialConversationId, AccountId = AccountId,
                SourceConversationId = "official-account-1", Kind = ConversationKind.Direct, Title = "Official bucket",
            },
        ], CancellationToken.None).ConfigureAwait(false);
        await store.UpsertMessagesAsync(CorpusMessages(), CancellationToken.None).ConfigureAwait(false);
        return store;
    }

    /// <summary>Every corpus document as one canonical message, in corpus order.</summary>
    public static IReadOnlyList<CanonicalMessage> CorpusMessages() =>
        [.. SearchBenchmarkCorpus.Documents.Select((document, ordinal) => ToMessage(document, ordinal))];

    /// <summary>Maps one corpus document onto the canonical message the archive would hold.</summary>
    public static CanonicalMessage ToMessage(SearchBenchmarkDocument document, int ordinal)
    {
        var conversationId = ConversationIds[document.ConversationId];
        var sender = ordinal % 2 == 0 ? AliceId : BobId;
        return new CanonicalMessage
        {
            Id = document.DocumentId,
            ConversationId = conversationId,
            SenderId = sender,
            OccurredAt = At(ordinal),
            Type = CanonicalTypeOf(document),
            Text = document.SemanticText,
            Payload = PayloadOf(document),
            Source = new SourceProvenance
            {
                SourceProfileId = "wxid_search_fixture",
                SourceConversationId = $"src:{document.ConversationId}",
                SourceMessageId = $"srcmsg:{document.DocumentId}",
                SourceType = "1",
                SourcePartition = "message_0",
                SourceOrderKey = ordinal.ToString("d4", System.Globalization.CultureInfo.InvariantCulture),
                AdapterName = "wechat-windows",
                AdapterVersion = "0.1.0",
                SourceVersion = "4.1.13.12",
            },
        };
    }

    /// <summary>
    /// The canonical semantic type implied by the document's searchable payload fields. Types with
    /// no searchable payload field collapse to <c>text</c>.
    /// </summary>
    public static CanonicalMessageType CanonicalTypeOf(SearchBenchmarkDocument document)
    {
        var fields = document.PayloadFields.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        if (fields.Contains("filename"))
            return CanonicalMessageType.File;
        if (fields.Contains("app_name"))
            return CanonicalMessageType.MiniProgram;
        if (fields.Contains("label") || fields.Contains("address"))
            return CanonicalMessageType.Location;
        if (fields.Contains("display_name"))
            return CanonicalMessageType.ContactCard;
        if (fields.Any(key => key.StartsWith("item_text_", StringComparison.Ordinal)))
            return CanonicalMessageType.ForwardBundle;
        if (fields.Contains("source_app"))
            return CanonicalMessageType.AppShare;
        if (fields.Contains("title") || fields.Contains("description"))
            return CanonicalMessageType.Link;
        return CanonicalMessageType.Text;
    }

    /// <summary>
    /// The canonical payload JsonObject for the document, using the MESSAGE_SCHEMA wire keys. Only
    /// searchable fields are needed by the index; the shape stays canonical anyway.
    /// </summary>
    public static JsonObject? PayloadOf(SearchBenchmarkDocument document)
    {
        var fields = document.PayloadFields;
        if (fields.Count == 0)
            return null;

        var type = CanonicalTypeOf(document);
        JsonObject payload;
        switch (type)
        {
            case CanonicalMessageType.File:
                payload = new JsonObject { ["filename"] = ValueOf(fields, "filename") };
                break;
            case CanonicalMessageType.MiniProgram:
                payload = new JsonObject
                {
                    ["app_name"] = ValueOf(fields, "app_name"),
                    ["title"] = ValueOf(fields, "title"),
                };
                break;
            case CanonicalMessageType.Location:
                payload = new JsonObject
                {
                    ["label"] = ValueOf(fields, "label"),
                    ["address"] = ValueOf(fields, "address"),
                };
                break;
            case CanonicalMessageType.ContactCard:
                payload = new JsonObject { ["display_name"] = ValueOf(fields, "display_name") };
                break;
            case CanonicalMessageType.ForwardBundle:
            {
                payload = new JsonObject { ["title"] = ValueOf(fields, "title") };
                var items = new JsonArray();
                foreach (var field in fields.Where(field => field.Key.StartsWith("item_text_", StringComparison.Ordinal)))
                {
                    items.Add(new JsonObject { ["text"] = field.Value });
                }

                payload["items"] = items;
                break;
            }
            case CanonicalMessageType.AppShare:
                payload = new JsonObject
                {
                    ["source_app"] = ValueOf(fields, "source_app"),
                    ["title"] = ValueOf(fields, "title"),
                    ["description"] = ValueOf(fields, "description"),
                };
                break;
            case CanonicalMessageType.Link:
                payload = new JsonObject
                {
                    ["title"] = ValueOf(fields, "title"),
                    ["description"] = ValueOf(fields, "description"),
                };
                break;
            default:
                return null;
        }

        return payload;
    }

    private static string? ValueOf(IReadOnlyList<KeyValuePair<string, string>> fields, string key) =>
        fields.FirstOrDefault(field => field.Key == key) is { } match ? match.Value : null;
}
