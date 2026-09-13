using System.Text.Json;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Adapter-compatibility tests against the real local WeChat installation.
/// <para>
/// These are the DoD-2..DoD-5 verifications: the source is discovered, conversations are
/// enumerated, a real conversation's records are read through the adapter, and every record
/// is normalized into the canonical schema. They are skipped automatically when no usable
/// WeChat installation is present, and they never assert on message content — only on
/// structure and counts — so no private data can leak into the test output.
/// </para>
/// </summary>
public sealed class WeChatAdapterIntegrationTests
{
    [WeChatEnvironmentFact]
    public async Task SourceIsDiscoveredWithClientVersionAndAccounts()
    {
        using var adapter = new WeChatWindowsSourceAdapter();

        var descriptor = await adapter.DescribeSourceAsync(CancellationToken.None);

        Assert.True(descriptor.IsAvailable, descriptor.UnavailableReason);
        Assert.Equal(WeChatWindowsSourceAdapter.Name, descriptor.AdapterName);
        Assert.False(string.IsNullOrWhiteSpace(descriptor.SourceVersion));

        var accounts = await adapter.ListAccountsAsync(CancellationToken.None);
        Assert.NotEmpty(accounts);
        Assert.All(accounts, a => Assert.False(string.IsNullOrWhiteSpace(a.SourceProfileId)));
    }

    [WeChatEnvironmentFact]
    public async Task ConversationsAreEnumeratedAsDirectAndGroupChatsWithStableIds()
    {
        using var adapter = new WeChatWindowsSourceAdapter();
        var account = (await adapter.ListAccountsAsync(CancellationToken.None))[0];

        var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, CancellationToken.None);

        Assert.NotEmpty(conversations);
        Assert.Contains(conversations, c => c.Kind == ConversationKind.Direct);
        Assert.Contains(conversations, c => c.Kind == ConversationKind.Group);
        Assert.All(conversations, c => Assert.False(string.IsNullOrWhiteSpace(c.SourceConversationId)));

        // Titles are metadata; a conversation without a resolvable name falls back to its id
        // rather than becoming empty.
        Assert.All(conversations, c => Assert.False(string.IsNullOrWhiteSpace(c.Title)));

        // No duplicate upstream ids.
        Assert.Equal(
            conversations.Count,
            conversations.Select(c => c.SourceConversationId).Distinct(StringComparer.Ordinal).Count());
    }

    [WeChatEnvironmentFact]
    public async Task ParticipantsExposeRemarkAndNicknameSeparately()
    {
        using var adapter = new WeChatWindowsSourceAdapter();
        var account = (await adapter.ListAccountsAsync(CancellationToken.None))[0];

        var participants = await adapter.ListParticipantsAsync(account.SourceProfileId, CancellationToken.None);

        Assert.NotEmpty(participants);
        Assert.All(participants, p => Assert.False(string.IsNullOrWhiteSpace(p.SourceUserId)));
        Assert.Contains(participants, p => !string.IsNullOrWhiteSpace(p.Nickname));
    }

    [WeChatEnvironmentFact]
    public async Task ARealConversationCanBeReadEndToEndThroughTheAdapter()
    {
        using var adapter = new WeChatWindowsSourceAdapter();
        var account = (await adapter.ListAccountsAsync(CancellationToken.None))[0];
        var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, CancellationToken.None);

        // Several small conversations are read so that the DoD requirement (text plus several
        // non-text types observed in real data) is verified across the real data set rather
        // than depending on what one particular chat happens to contain.
        var candidates = await FindConversationsAsync(adapter, account.SourceProfileId, conversations);
        Assert.NotEmpty(candidates);

        var seen = 0;
        var readConversations = 0;
        var types = new Dictionary<CanonicalMessageType, int>();
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;
        var withoutStableId = 0;

        foreach (var candidate in candidates)
        {
            readConversations++;
            await foreach (var message in adapter.ReadMessagesAsync(
                account.SourceProfileId, candidate.SourceConversationId, CancellationToken.None))
            {
                seen++;

                // The adapter contract requires a stable source id for every record.
                if (string.IsNullOrWhiteSpace(message.SourceMessageId))
                {
                    withoutStableId++;
                }

                Assert.True(message.OccurredAt > DateTimeOffset.UnixEpoch);
                Assert.NotNull(message.Content);

                var normalized = Core.Normalization.MessageNormalizer.Normalize(
                    message,
                    new Core.Normalization.NormalizationContext
                    {
                        AccountId = "a_test",
                        ConversationId = "c_test",
                        SourceProfileId = account.SourceProfileId,
                        AdapterName = adapter.AdapterName,
                        AdapterVersion = adapter.AdapterVersion,
                        ResolveParticipantId = id => "u_" + id,
                    });

                Assert.False(string.IsNullOrEmpty(normalized.Text));
                types[normalized.Type] = types.GetValueOrDefault(normalized.Type) + 1;

                first = normalized.OccurredAt < first ? normalized.OccurredAt : first;
                last = normalized.OccurredAt > last ? normalized.OccurredAt : last;

                if (seen >= 6000)
                {
                    break;
                }
            }

            if (seen >= 6000 || types.Count >= 5 || readConversations >= 6)
            {
                break;
            }
        }

        Assert.True(seen > 0, "the selected conversations produced no records");
        Assert.Equal(0, withoutStableId);
        Assert.True(first <= last);

        // The DoD requires at least text plus several non-text types to be observed in the
        // real data set.
        Assert.Contains(CanonicalMessageType.Text, types.Keys);
        Assert.True(
            types.Count >= 3,
            $"expected at least three canonical types in real data, saw: {string.Join(", ", types.Keys)}");
    }

    [WeChatEnvironmentFact]
    public async Task RealConversationExportProducesADocsCompliantDataset()
    {
        using var temp = new Support.TempDirectory();
        using var adapter = new WeChatWindowsSourceAdapter();

        var account = (await adapter.ListAccountsAsync(CancellationToken.None))[0];
        var conversations = await adapter.ListConversationsAsync(account.SourceProfileId, CancellationToken.None);
        var candidate = (await FindConversationsAsync(adapter, account.SourceProfileId, conversations)).FirstOrDefault();
        Assert.NotNull(candidate);

        var store = new Infrastructure.Archive.SqliteArchiveStore(
            temp.Combine("archive.db"),
            new Support.FixedClock());
        var exporter = new Infrastructure.Export.JsonlDatasetExporter(store);
        var catalog = new Core.Services.SourceCatalogService(adapter);
        var importer = new Core.Services.ImportService(adapter, store, new Support.FixedClock());
        var workflow = new Core.Services.ArchiveWorkflow(catalog, importer, exporter, store, new Support.FixedClock());

        var output = temp.Combine("export");
        var result = await workflow.ExportConversationAsync(
            new Core.Services.ExportConversationRequest
            {
                SourceProfileId = account.SourceProfileId,
                SourceConversationId = candidate!.SourceConversationId,
                Kind = candidate.Kind,
                PeerSourceUserId = candidate.PeerSourceUserId,
                ConversationTitle = candidate.Title,
                OutputDirectory = output,
            },
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.RecordCount > 0);

        // The package must use stable-id paths, never the display name.
        var conversationId = result.ConversationIds.Single();
        var bucket = candidate.Kind == ConversationKind.Group ? "groups" : "direct";
        var folder = Path.Combine(output, "chats", bucket, conversationId);
        Assert.True(Directory.Exists(folder), $"expected export folder {folder}");
        Assert.DoesNotContain(candidate.Title ?? "\u0000", folder, StringComparison.Ordinal);

        var files = Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);

        var lines = 0;
        foreach (var file in files)
        {
            // conversation / year / month partitioning
            Assert.Equal(conversationId, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))));
            Assert.Matches(@"^\d{4}-\d{2}\.jsonl$", Path.GetFileName(file));

            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                Assert.Equal(conversationId, document.RootElement.GetProperty("conversation_id").GetString());
                Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("text").GetString()));
                lines++;
            }
        }

        Assert.Equal(result.RecordCount, lines);

        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        Assert.Equal("1.0", manifest.RootElement.GetProperty("export_schema_version").GetString());
        Assert.Equal(result.RecordCount, manifest.RootElement.GetProperty("record_count").GetInt32());
        Assert.Equal(result.UnknownCount, manifest.RootElement.GetProperty("unknown_count").GetInt32());

        Assert.True(File.Exists(Path.Combine(output, "identities.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "conversations.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "collections.yaml")));

        // Re-running the same export must not change the archive size.
        var before = await store.GetArchiveStatsAsync(CancellationToken.None);
        var second = await workflow.ExportConversationAsync(
            new Core.Services.ExportConversationRequest
            {
                SourceProfileId = account.SourceProfileId,
                SourceConversationId = candidate.SourceConversationId,
                Kind = candidate.Kind,
                PeerSourceUserId = candidate.PeerSourceUserId,
                ConversationTitle = candidate.Title,
                OutputDirectory = output,
            },
            null,
            CancellationToken.None);
        var after = await store.GetArchiveStatsAsync(CancellationToken.None);

        Assert.Equal(before.MessageCount, after.MessageCount);
        Assert.Equal(before.MessageCount, second.RecordCount);
    }

    private static async Task<List<SourceConversation>> FindConversationsAsync(
        ISourceAdapter adapter,
        string sourceProfileId,
        IReadOnlyList<SourceConversation> conversations)
    {
        // Smallest conversations with at least a handful of records, groups first so the group
        // path (member senders, system events, app messages) is what actually gets verified.
        var scored = new List<(SourceConversation Conversation, int Count)>();

        foreach (var conversation in conversations
            .Where(c => c.Kind is ConversationKind.Group or ConversationKind.Direct)
            .Take(120))
        {
            SourceConversationDetail detail;
            try
            {
                detail = await adapter.DescribeConversationAsync(
                    sourceProfileId, conversation.SourceConversationId, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (detail.MessageCount is >= 5 and < 3000)
            {
                scored.Add((conversation, detail.MessageCount));
            }
        }

        return
        [
            .. scored
                .OrderByDescending(s => s.Conversation.Kind == ConversationKind.Group)
                .ThenByDescending(s => s.Count)
                .Select(s => s.Conversation)
                .Take(6)
        ];
    }
}
