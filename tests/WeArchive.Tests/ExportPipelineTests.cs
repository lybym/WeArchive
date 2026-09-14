using System.Text;
using System.Text.Json;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Tests.Support;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WeArchive.Tests;

/// <summary>
/// End-to-end: fixture source -&gt; adapter -&gt; normalizer -&gt; SQLite archive -&gt; JSONL dataset.
/// This is the M0 acceptance path from docs/ROADMAP.md.
/// </summary>
public sealed class ExportPipelineTests
{
    private sealed record Harness(
        FixtureSourceAdapter Adapter,
        SqliteArchiveStore Store,
        JsonlDatasetExporter Exporter,
        ArchiveWorkflow Workflow);

    private static Harness CreateHarness(TempDirectory temp)
    {
        var adapter = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new JsonlDatasetExporter(store);
        var catalog = new SourceCatalogService(adapter);
        var importer = new ImportService(adapter, store, clock);
        var workflow = new ArchiveWorkflow(catalog, importer, exporter, store, clock);

        return new Harness(adapter, store, exporter, workflow);
    }

    private static ExportConversationRequest GroupRequest(string outputDirectory) => new()
    {
        SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
        SourceConversationId = FixtureSourceAdapter.GroupConversation,
        Kind = ConversationKind.Group,
        ConversationTitle = "华东产品创新中心工作群",
        OutputDirectory = outputDirectory,
    };

    private static List<string> JsonlLines(string path) =>
        [.. File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0)];

    [Fact]
    public async Task GroupExportProducesTheDocumentedPackageLayout()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "identities.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "conversations.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "collections.yaml")));

        var conversationId = result.ConversationIds.Single();
        Assert.StartsWith("g_", conversationId, StringComparison.Ordinal);

        // Monthly partitioning under a stable-id folder.
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", conversationId, "2026", "2026-01.jsonl")));
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", conversationId, "2026", "2026-02.jsonl")));
    }

    [Fact]
    public async Task DirectExportUsesTheDirectFolderAndThePeerStableId()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(
            new ExportConversationRequest
            {
                SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
                SourceConversationId = FixtureSourceAdapter.DirectConversation,
                Kind = ConversationKind.Direct,
                PeerSourceUserId = FixtureSourceAdapter.Alice,
                OutputDirectory = output,
            },
            null,
            CancellationToken.None);

        var conversationId = result.ConversationIds.Single();
        Assert.StartsWith("u_", conversationId, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(output, "chats", "direct", conversationId)));
        Assert.False(Directory.Exists(Path.Combine(output, "chats", "groups", conversationId)));
    }

    [Fact]
    public async Task EveryJsonlRecordConformsToTheCanonicalEnvelope()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = result.ConversationIds.Single();
        var folder = Path.Combine(output, "chats", "groups", conversationId);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories))
        {
            foreach (var line in JsonlLines(file))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                Assert.Equal(conversationId, root.GetProperty("conversation_id").GetString());
                Assert.StartsWith("m_", root.GetProperty("id").GetString(), StringComparison.Ordinal);
                Assert.True(root.TryGetProperty("sender_id", out _));
                Assert.True(root.TryGetProperty("reply_to", out _));
                Assert.True(root.TryGetProperty("payload", out _));
                Assert.Contains(root.GetProperty("type").GetString(),
                    new[]
                    {
                        "text", "image", "voice", "video", "file", "link", "app_share", "mini_program",
                        "forward_bundle", "location", "contact_card", "system", "revoke", "red_packet",
                        "transfer", "emoji", "unknown",
                    });

                var time = root.GetProperty("time").GetString()!;
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2}$", time);

                Assert.True(root.GetProperty("text").GetString()!.Length > 0);
                Assert.True(root.TryGetProperty("source", out var source));
                Assert.True(source.TryGetProperty("source_message_id", out _));

                Assert.True(seen.Add(root.GetProperty("id").GetString()!), "duplicate record id in export");
                total++;
            }
        }

        Assert.Equal(result.RecordCount, total);

        // Nothing may be dropped: every fixture record must reach the dataset.
        Assert.Equal(
            FixtureSourceAdapter.BuildMessages(FixtureSourceAdapter.GroupConversation).Count,
            total);
    }

    [Fact]
    public async Task UnknownRecordsAreExportedAndCounted()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        Assert.True(result.UnknownCount >= 1);

        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("unknown_count").GetInt32() >= 1);
        Assert.True(manifest.RootElement.GetProperty("unsupported_count").GetInt32() >= 1);

        var conversationId = result.ConversationIds.Single();
        var all = Directory
            .EnumerateFiles(Path.Combine(output, "chats", "groups", conversationId), "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(JsonlLines)
            .Select(line => JsonDocument.Parse(line))
            .ToList();

        try
        {
            var unknown = all.Where(d => d.RootElement.GetProperty("type").GetString() == "unknown").ToList();
            Assert.NotEmpty(unknown);
            var payload = unknown[0].RootElement.GetProperty("payload");
            Assert.Equal("1000007", payload.GetProperty("source_type").GetString());
        }
        finally
        {
            foreach (var document in all)
            {
                document.Dispose();
            }
        }
    }

    [Fact]
    public async Task MediaFilesLinksAndRepliesKeepTheirSemantics()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = result.ConversationIds.Single();

        var records = Directory
            .EnumerateFiles(Path.Combine(output, "chats", "groups", conversationId), "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(JsonlLines)
            .Select(l => JsonDocument.Parse(l))
            .ToList();

        try
        {
            JsonElement Find(string type, Func<JsonElement, bool> predicate) =>
                records.Select(r => r.RootElement)
                    .First(e => e.GetProperty("type").GetString() == type && predicate(e));

            // image / voice / video are textual events only
            Assert.Equal("[图片]", Find("image", _ => true).GetProperty("text").GetString());
            Assert.Equal("[视频]", Find("video", _ => true).GetProperty("text").GetString());
            var voice = Find("voice", e => e.GetProperty("payload").GetProperty("duration_seconds").ValueKind != JsonValueKind.Null);
            Assert.Equal(37, voice.GetProperty("payload").GetProperty("duration_seconds").GetInt32());

            // file keeps the original name, and stays nameless when WeChat had none
            var file = Find("file", e => e.GetProperty("payload").GetProperty("filename").ValueKind == JsonValueKind.String);
            Assert.Equal("[文件] 华东中心项目汇报V8.pptx", file.GetProperty("text").GetString());
            var unnamed = Find("file", e => e.GetProperty("payload").GetProperty("filename").ValueKind == JsonValueKind.Null);
            Assert.Equal("[文件]", unnamed.GetProperty("text").GetString());

            // link exposes a real original url
            var link = Find("link", e => e.GetProperty("payload").GetProperty("original_url").ValueKind == JsonValueKind.String);
            Assert.Equal("https://example.com/article/123", link.GetProperty("payload").GetProperty("original_url").GetString());

            // a wrapper-only url is never promoted to original_url
            var wrapper = Find("link", e => e.GetProperty("payload").GetProperty("fallback_url").ValueKind == JsonValueKind.String);
            Assert.Equal(JsonValueKind.Null, wrapper.GetProperty("payload").GetProperty("original_url").ValueKind);

            // app share keeps the source application
            var appShare = Find("app_share", _ => true);
            Assert.Equal("小红书", appShare.GetProperty("payload").GetProperty("source_app").GetString());

            // mini program is its own type
            var mini = Find("mini_program", _ => true);
            Assert.Equal("pages/product?id=123", mini.GetProperty("payload").GetProperty("page_path").GetString());

            // forwarded bundle keeps nested items
            var bundle = Find("forward_bundle", _ => true);
            Assert.Equal(2, bundle.GetProperty("payload").GetProperty("item_count").GetInt32());
            Assert.True(bundle.GetProperty("payload").GetProperty("items").GetArrayLength() >= 1);

            // the reply keeps a snapshot and, because the target was not exported, no id
            var reply = records.Select(r => r.RootElement)
                .First(e => e.GetProperty("reply_to").ValueKind == JsonValueKind.Object);
            var replyTo = reply.GetProperty("reply_to");
            Assert.Equal("下午三点开会。", replyTo.GetProperty("text").GetString());
            Assert.Equal(JsonValueKind.Null, replyTo.GetProperty("message_id").ValueKind);
        }
        finally
        {
            foreach (var document in records)
            {
                document.Dispose();
            }
        }
    }

    [Fact]
    public async Task IdentitiesFollowTheLatestRemarkRule()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);

        var yaml = await File.ReadAllTextAsync(Path.Combine(output, "identities.yaml"));
        var document = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build()
            .Deserialize<Dictionary<string, object>>(yaml);

        var users = (Dictionary<object, object>)document["users"];

        var withRemark = users.Values
            .Cast<Dictionary<object, object>>()
            .First(u => (string)u["source_user_id"] == FixtureSourceAdapter.Alice);
        Assert.Equal("张三", withRemark["remark"]);
        Assert.Equal("三哥", withRemark["nickname"]);
        Assert.Equal("张三", withRemark["display_name"]);

        var withoutRemark = users.Values
            .Cast<Dictionary<object, object>>()
            .First(u => (string)u["source_user_id"] == FixtureSourceAdapter.Bob);
        Assert.Equal(string.Empty, withoutRemark["remark"]);
        Assert.Equal("Kevin", withoutRemark["nickname"]);

        // The nickname must never be promoted into the display name.
        Assert.Equal(string.Empty, withoutRemark["display_name"]);
    }

    [Fact]
    public async Task ConversationCatalogAndCollectionsAreWritten()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = result.ConversationIds.Single();

        var conversations = await File.ReadAllTextAsync(Path.Combine(output, "conversations.yaml"));
        Assert.Contains(conversationId, conversations, StringComparison.Ordinal);
        Assert.Contains("type: group", conversations, StringComparison.Ordinal);
        Assert.Contains("current_name:", conversations, StringComparison.Ordinal);

        var collections = await File.ReadAllTextAsync(Path.Combine(output, "collections.yaml"));
        Assert.Contains("collections", collections, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestDeclaresSchemasCountersAndFiles()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        var root = manifest.RootElement;

        Assert.Equal("1.0", root.GetProperty("export_schema_version").GetString());
        Assert.Equal("1.0", root.GetProperty("message_schema_version").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("exporter_version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("created_at").GetString()));
        Assert.StartsWith("a_", root.GetProperty("source_account_id").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, root.GetProperty("conversation_ids").GetArrayLength());
        Assert.True(root.GetProperty("record_count").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("time_range").ValueKind);

        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "identities");
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "conversations");
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "collections");
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "timeline");

        // A consumer must be able to see which month partitions exist without reading them.
        var timeline = files.First(f => f.GetProperty("kind").GetString() == "timeline");
        Assert.Equal(2026, timeline.GetProperty("year").GetInt32());
        Assert.True(timeline.GetProperty("month").GetInt32() >= 1);

        // Provenance and completeness travel with the manifest.
        Assert.Equal("fixture", root.GetProperty("source_adapter").GetString());
        Assert.Equal("fixture-1", root.GetProperty("source_version").GetString());

        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.NotEmpty(diagnostics);
        Assert.Contains(diagnostics, d => d.GetProperty("code").GetString() == "unknown_message_type");
        Assert.All(diagnostics, d => Assert.True(d.GetProperty("count").GetInt32() >= 1));
    }

    [Fact]
    public async Task ReExportIsDeterministicAndCreatesNoDuplicatePartitions()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var first = Snapshot(output);

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var second = Snapshot(output);

        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (file, content) in first)
        {
            Assert.Equal(content, second[file]);
        }
    }

    [Fact]
    public async Task ReExportPreservesUserMaintainedCatalogs()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = result.ConversationIds.Single();

        // A user edits the alias, adds a collection and corrects a display name.
        await File.WriteAllTextAsync(
            Path.Combine(output, "conversations.yaml"),
            $"schema_version: 1.0\nconversations:\n  {conversationId}:\n    type: group\n    current_name: 华东产品创新中心工作群\n    alias: east-product-center\n");

        await File.WriteAllTextAsync(
            Path.Combine(output, "collections.yaml"),
            "schema_version: 1.0\ncollections:\n  east-product-center:\n    conversations:\n" +
            $"      - {conversationId}\n");

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);

        var conversations = await File.ReadAllTextAsync(Path.Combine(output, "conversations.yaml"));
        Assert.Contains("east-product-center", conversations, StringComparison.Ordinal);

        var collections = await File.ReadAllTextAsync(Path.Combine(output, "collections.yaml"));
        Assert.Contains("east-product-center", collections, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReimportingTheSameConversationDoesNotDuplicateArchivedMessages()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");
        var request = GroupRequest(output);

        await harness.Workflow.ExportConversationAsync(request, null, CancellationToken.None);
        var afterFirst = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        var second = await harness.Workflow.ExportConversationAsync(request, null, CancellationToken.None);
        var afterSecond = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        Assert.Equal(afterFirst.MessageCount, afterSecond.MessageCount);
        Assert.Equal(afterFirst.MessageCount, second.RecordCount);
    }

    [Fact]
    public async Task ProgressIsReportedForTheWholeOperation()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var stages = new List<string>();
        var progress = new Progress<OperationProgress>(p => stages.Add(p.Stage));

        await harness.Workflow.ExportConversationAsync(GroupRequest(temp.Combine("export")), progress, CancellationToken.None);

        Assert.Contains(OperationStages.ProbingSource, stages);
        Assert.Contains(OperationStages.Completed, stages);
    }

    [Fact]
    public async Task ACancelledReExportLeavesThePreviouslyExportedDatasetIntact()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        // Establish a complete dataset.
        var first = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = first.ConversationIds.Single();
        var timelineFolder = Path.Combine(output, "chats", "groups", conversationId);

        var priorManifest = await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"));
        var priorIdentities = await File.ReadAllTextAsync(Path.Combine(output, "identities.yaml"));
        var priorPartitions = Directory
            .EnumerateFiles(timelineFolder, "*.jsonl", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        // Re-export through the exporter directly with a progress callback that cancels as
        // soon as the first (and only) timeline has been staged, i.e. before the catalogs
        // are written and before anything is committed.
        using var cts = new CancellationTokenSource();
        var cancelProgress = new CancelOnFirstReport<ExportProgress>(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Exporter.ExportAsync(
                new ExportRequest
                {
                    OutputDirectory = output,
                    AccountId = StableIds.Account(harness.Adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId),
                    ConversationIds = [conversationId],
                },
                cancelProgress,
                cts.Token));

        // The prior package must survive untouched, with no leftover staging directory.
        Assert.Equal(priorManifest, await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        Assert.Equal(priorIdentities, await File.ReadAllTextAsync(Path.Combine(output, "identities.yaml")));
        Assert.Equal(priorPartitions, Directory
            .EnumerateFiles(timelineFolder, "*.jsonl", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList());
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Combine(output, "chats", "groups")),
            d => Path.GetFileName(d).StartsWith("wearchive-export-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWriteFailureDuringReExportLeavesThePreviouslyExportedDatasetIntact()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        var first = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = first.ConversationIds.Single();
        var timelineFolder = Path.Combine(output, "chats", "groups", conversationId);

        var priorManifest = await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"));
        var priorIdentities = await File.ReadAllTextAsync(Path.Combine(output, "identities.yaml"));
        var priorConversations = await File.ReadAllTextAsync(Path.Combine(output, "conversations.yaml"));
        var priorPartitions = Directory
            .EnumerateFiles(timelineFolder, "*.jsonl", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        // Hold an exclusive handle on the identities catalog's staging path so the catalog
        // write cannot truncate it. The staged timeline must be discarded and the prior
        // package preserved, never partially overwritten.
        var identitiesStaging = Path.Combine(output, "identities.yaml.wearchive-export-staging");
        using (new FileStream(identitiesStaging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() =>
                harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None));
        }

        Assert.Equal(priorManifest, await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        Assert.Equal(priorIdentities, await File.ReadAllTextAsync(Path.Combine(output, "identities.yaml")));
        Assert.Equal(priorConversations, await File.ReadAllTextAsync(Path.Combine(output, "conversations.yaml")));
        Assert.Equal(priorPartitions, Directory
            .EnumerateFiles(timelineFolder, "*.jsonl", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList());
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(Path.Combine(output, "chats", "groups")),
            d => Path.GetFileName(d).StartsWith("wearchive-export-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACrashedCommitIsRecoveredOnTheNextExportNotLost()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        // A complete first export of the real conversation.
        var first = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var realConversationId = first.ConversationIds.Single();
        var groupsBucket = Path.Combine(output, "chats", "groups");

        // Simulate a crash mid-commit of an UNRELATED conversation: its final directory is
        // absent (the prior data was renamed to a backup), and a partial staging directory
        // remains. This is exactly the dangerous interval the recoverable swap must
        // survive.
        const string crashedId = "g_crashed_fake_conv";
        var crashedFinal = Path.Combine(groupsBucket, crashedId);
        var crashedBackup = Path.Combine(groupsBucket, crashedId + ".wearchive-backup-deadbeef");
        var crashedStaging = Path.Combine(groupsBucket, "wearchive-export-staging-deadbeef");
        Directory.CreateDirectory(Path.Combine(crashedBackup, "2026"));
        File.WriteAllText(
            Path.Combine(crashedBackup, "2026", "2026-01.jsonl"),
            "{\"id\":\"m_old\"}\n"); // the last known-good timeline
        Directory.CreateDirectory(crashedStaging);

        // A fresh export must recover the backup (rename it back to final) rather than
        // deleting it, and sweep the orphaned staging.
        var second = await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);

        // The crashed conversation's last good dataset was recovered into its final path.
        Assert.True(Directory.Exists(crashedFinal), "the crashed conversation's backup must be recovered, not lost");
        Assert.Equal("{\"id\":\"m_old\"}\n", await File.ReadAllTextAsync(
            Path.Combine(crashedFinal, "2026", "2026-01.jsonl")));

        // The real conversation exported normally, and no crash artifacts remain.
        Assert.True(second.Succeeded);
        Assert.True(Directory.Exists(Path.Combine(groupsBucket, realConversationId)));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(groupsBucket),
            d => Path.GetFileName(d).Contains(".wearchive-backup-", StringComparison.Ordinal)
              || Path.GetFileName(d).StartsWith("wearchive-export-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALeftoverBackupIsRestoredOverThePossiblyNotDurableFinalOnTheNextExport()
    {
        using var temp = new TempDirectory();
        var harness = CreateHarness(temp);
        var output = temp.Combine("export");

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var groupsBucket = Path.Combine(output, "chats", "groups");

        // Simulate a crash after the replacement move but before the backup delete: a new
        // final is in place, but the prior-good backup remains. Since the new final's
        // durability cannot be confirmed post-hoc, the next export must restore the backup
        // (the last good dataset) rather than sweeping it.
        const string convId = "g_crashed_fake_conv";
        var final = Path.Combine(groupsBucket, convId);
        var leftoverBackup = Path.Combine(groupsBucket, convId + ".wearchive-backup-deadbeef");
        Directory.CreateDirectory(Path.Combine(final, "2026"));
        File.WriteAllText(Path.Combine(final, "2026", "2026-01.jsonl"), "{\"id\":\"m_new\"}\n");
        Directory.CreateDirectory(Path.Combine(leftoverBackup, "2026"));
        File.WriteAllText(Path.Combine(leftoverBackup, "2026", "2026-01.jsonl"), "{\"id\":\"m_old\"}\n");

        await harness.Workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);

        // The prior-good backup was restored over the new final; the new (possibly not
        // durable) final was discarded.
        Assert.True(Directory.Exists(final));
        Assert.Equal("{\"id\":\"m_old\"}\n", await File.ReadAllTextAsync(
            Path.Combine(final, "2026", "2026-01.jsonl")));
        Assert.False(Directory.Exists(leftoverBackup));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(groupsBucket),
            d => Path.GetFileName(d).Contains(".wearchive-backup-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailureAtTheDurabilityBoundaryRestoresTheLastGoodDataset()
    {
        using var temp = new TempDirectory();
        var adapter = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new FaultingJsonlDatasetExporter(store);
        var catalog = new SourceCatalogService(adapter);
        var importer = new ImportService(adapter, store, clock);
        var workflow = new ArchiveWorkflow(catalog, importer, exporter, store, clock);
        var output = temp.Combine("export");

        // First export: the durability barrier (DurableCommit call #1) succeeds; the old
        // dataset is established and driven through the real rename/move/durable-commit/
        // delete sequence.
        var first = await workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None);
        var conversationId = first.ConversationIds.Single();
        var final = Path.Combine(output, "chats", "groups", conversationId);

        // Mark the established good dataset so the restore is observable: the re-export's new
        // staging will not contain this marker.
        await File.WriteAllTextAsync(Path.Combine(final, "BOUNDARY-MARKER"), "old", CancellationToken.None);

        // Re-export: the durability barrier throws on the second commit, simulating a power
        // loss at the boundary after the replacement move and before backup deletion.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            workflow.ExportConversationAsync(GroupRequest(output), null, CancellationToken.None));

        // The not-yet-durable new final was discarded and the prior-good backup restored, so
        // the marker (present only in the old data) survives.
        Assert.True(File.Exists(Path.Combine(final, "BOUNDARY-MARKER")));
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(final, "BOUNDARY-MARKER")));

        // No crash artifacts remain.
        var groupsBucket = Path.Combine(output, "chats", "groups");
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(groupsBucket),
            d => Path.GetFileName(d).Contains(".wearchive-backup-", StringComparison.Ordinal)
              || Path.GetFileName(d).StartsWith("wearchive-export-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public void TheProductionDurabilityBarrierSurfacesANativeCreateFileFailure()
    {
        // Regression for the review finding on commit c7fc954: the durability barrier must
        // surface a real native failure rather than swallowing it, so CommitConversation's
        // catch can discard the not-yet-durable replacement and restore the prior-good backup.
        // This drives the real (non-overridden) DurableCommit -> NativeMethods.FsyncDirectory
        // path with an actual CreateFileW failure on a directory that does not exist, instead
        // of the override that the existing boundary test relies on.
        using var temp = new TempDirectory();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), new FixedClock());
        var exporter = new ProductionBarrierJsonlDatasetExporter(store);
        var missing = temp.Combine("does-not-exist-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<System.ComponentModel.Win32Exception>(
            () => exporter.InvokeDurableCommit(missing));

        // A real Win32 error code was surfaced via Marshal.GetLastWin32Error (2 =
        // ERROR_FILE_NOT_FOUND or 3 = ERROR_PATH_NOT_FOUND on Windows), proving the barrier no
        // longer swallows native failures. FlushFileBuffers uses the same surface pattern.
        Assert.NotEqual(0, ex.NativeErrorCode);
    }

    /// <summary>
    /// Reports synchronously so a test can cancel the token exactly when the exporter
    /// reports progress, rather than through the async <see cref="Progress{T}"/> post.
    /// </summary>
    private sealed class CancelOnFirstReport<T>(CancellationTokenSource cts) : IProgress<T>
    {
        public void Report(T value) => cts.Cancel();
    }

    /// <summary>
    /// Throws on the second <see cref="JsonlDatasetExporter.DurableCommit"/> call to
    /// fault-inject the durability boundary (after the replacement move, before backup
    /// deletion) that the production commit must survive.
    /// </summary>
    private sealed class FaultingJsonlDatasetExporter(IArchiveStore archive) : JsonlDatasetExporter(archive)
    {
        private int _commits;

        protected internal override void DurableCommit(string directory)
        {
            if (++_commits == 2)
            {
                throw new InvalidOperationException("simulated power loss at the durability boundary");
            }
        }
    }

    /// <summary>
    /// Exposes the production DurableCommit unchanged (it does not override it) so a test can
    /// prove the real NativeMethods.FsyncDirectory path surfaces a native failure instead of
    /// swallowing it.
    /// </summary>
    private sealed class ProductionBarrierJsonlDatasetExporter(IArchiveStore archive) : JsonlDatasetExporter(archive)
    {
        public void InvokeDurableCommit(string directory) => DurableCommit(directory);
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // created_at and exporter metadata are explicitly allowed to differ.
            result[relative] = relative == "manifest.json"
                ? StripCreatedAt(File.ReadAllText(file))
                : File.ReadAllText(file);
        }

        return result;
    }

    private static string StripCreatedAt(string manifestJson)
    {
        using var document = JsonDocument.Parse(manifestJson);
        var properties = document.RootElement.EnumerateObject()
            .Where(p => p.Name != "created_at")
            .Select(p => p.Name + "=" + p.Value.GetRawText());
        return string.Join('|', properties);
    }
}

