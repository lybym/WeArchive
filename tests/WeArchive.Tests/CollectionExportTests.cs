using System.Text.Json;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Collection-scoped export and time-range selection over the canonical archive
/// (issue #106, docs/PRD.md FR-18/FR-23, docs/EXPORT_PRD.md sections 7, 8 and 14).
/// <para>
/// The harness seeds the fixture group and direct conversations through
/// <see cref="ImportService"/> — the same publication engine an explicit <c>sync</c> drives — and
/// measures the canonical-archive-to-dataset boundary: exactly the selected members/dates appear
/// with stable IDs and deterministic bytes, missing members stay explicit in the result and in
/// <c>manifest.json</c>, and the R1 publication behavior is unchanged.
/// </para>
/// </summary>
public sealed class CollectionExportTests
{
    private const string CollectionName = "east-product-center";

    /// <summary>A well-formed stable conversation id that no fixture test ever archives.</summary>
    private const string NeverArchivedMemberId = "u_000000000000beef";

    /// <summary>A membership entry that is not a stable conversation id at all.</summary>
    private const string InvalidMemberEntry = "wxid_typo";

    private sealed record Harness(
        FixtureSourceAdapter Adapter,
        SqliteArchiveStore Store,
        StubCollectionCatalogSource CollectionSource,
        ArchiveWorkflow Workflow);

    private static async Task<Harness> CreateHarnessAsync(TempDirectory temp)
    {
        var adapter = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new JsonlDatasetExporter(store);
        var importer = new ImportService(adapter, store, clock);
        var collectionSource = new StubCollectionCatalogSource();
        var workflow = new ArchiveWorkflow(
            exporter, store, new CollectionCatalogService(collectionSource), clock);

        await importer.ImportConversationAsync(new ImportRequest
        {
            SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
            SourceConversationId = FixtureSourceAdapter.GroupConversation,
            Kind = ConversationKind.Group,
            ConversationTitle = "华东产品创新中心工作群",
        }, null, CancellationToken.None);

        await importer.ImportConversationAsync(new ImportRequest
        {
            SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
            SourceConversationId = FixtureSourceAdapter.DirectConversation,
            Kind = ConversationKind.Direct,
            PeerSourceUserId = FixtureSourceAdapter.Alice,
            ConversationTitle = "张三",
        }, null, CancellationToken.None);

        return new Harness(adapter, store, collectionSource, workflow);
    }

    private static string AccountStableId(FixtureSourceAdapter adapter) =>
        StableIds.Account(adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId);

    private static string GroupStableId(FixtureSourceAdapter adapter) =>
        StableIds.Conversation(
            AccountStableId(adapter), ConversationKind.Group, FixtureSourceAdapter.GroupConversation, null);

    private static string DirectStableId(FixtureSourceAdapter adapter) =>
        StableIds.Conversation(
            AccountStableId(adapter), ConversationKind.Direct, FixtureSourceAdapter.DirectConversation, FixtureSourceAdapter.Alice);

    private static void SeedCollection(Harness harness, string name, params string[] members)
    {
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = CollectionCatalog.SupportedSchemaVersion,
            Collections = new Dictionary<string, CollectionEntryDocument>(StringComparer.Ordinal)
            {
                [name] = new CollectionEntryDocument { Conversations = [.. members] },
            },
        };
    }

    private static ExportCollectionRequest CollectionRequest(Harness harness, string output) => new()
    {
        CollectionName = CollectionName,
        OutputDirectory = output,
    };

    private static JsonDocument ReadManifest(string root) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));

    /// <summary>Stable IDs of the participants the fixture senders resolve to.</summary>
    private static string AliceStableId(FixtureSourceAdapter adapter) =>
        StableIds.Participant(AccountStableId(adapter), FixtureSourceAdapter.Alice);

    private static string BobStableId(FixtureSourceAdapter adapter) =>
        StableIds.Participant(AccountStableId(adapter), FixtureSourceAdapter.Bob);

    private static string CarolStableId(FixtureSourceAdapter adapter) =>
        StableIds.Participant(AccountStableId(adapter), FixtureSourceAdapter.Carol);

    private static string MeStableId(FixtureSourceAdapter adapter) =>
        StableIds.Participant(AccountStableId(adapter), FixtureSourceAdapter.FixtureAccountId);

    [Fact]
    public async Task CollectionScopedExportProducesExactlyTheArchivedMemberPartitions()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var direct = DirectStableId(harness.Adapter);
        SeedCollection(harness, CollectionName, group, direct);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportCollectionAsync(
            CollectionRequest(harness, output), null, CancellationToken.None);

        // Exactly the resolved membership, in first-declaration order, with stable ids.
        Assert.True(result.Succeeded);
        Assert.Equal(new[] { group, direct }, result.ConversationIds);
        Assert.Equal(27, result.RecordCount); // 23 group + 4 direct fixture records

        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", group, "2026", "2026-01.jsonl")));
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", group, "2026", "2026-02.jsonl")));
        Assert.True(File.Exists(Path.Combine(output, "chats", "direct", direct, "2026", "2026-01.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(output, "chats", "groups", direct)));
        Assert.False(Directory.Exists(Path.Combine(output, "chats", "direct", group)));

        var scope = result.Scope!;
        Assert.Equal("collection", scope.Kind);
        Assert.Equal(CollectionName, scope.CollectionName);
        Assert.Equal(2, scope.MemberConversationCount);
        Assert.Equal(2, scope.ArchivedMemberConversationCount);
        Assert.Empty(scope.UnarchivedConversationIds!);
        Assert.Equal(0, scope.InvalidMemberCount);
        Assert.Equal(0, scope.DuplicateMemberCount);
    }

    [Fact]
    public async Task CollectionScopedReExportIsDeterministicByteForByte()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter), DirectStableId(harness.Adapter));
        var output = temp.Combine("export");

        await harness.Workflow.ExportCollectionAsync(
            CollectionRequest(harness, output), null, CancellationToken.None);
        var first = Snapshot(output);

        await harness.Workflow.ExportCollectionAsync(
            CollectionRequest(harness, output), null, CancellationToken.None);
        var second = Snapshot(output);

        // The fixed clock pins created_at, so every package byte is reproducible and no duplicate
        // temporal partition can appear (docs/EXPORT_PRD.md section 15).
        Assert.Equal(first.Keys.OrderBy(k => k, StringComparer.Ordinal), second.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (file, content) in first)
        {
            Assert.Equal(content, second[file]);
        }
    }

    [Fact]
    public async Task CollectionScopeReportsUnarchivedInvalidAndDuplicateMembers()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var direct = DirectStableId(harness.Adapter);

        // The declared set carries an unarchived member, a non-stable-id entry and a duplicate.
        // None of them may silently narrow or fail the export: the archived members are exported
        // and the rest is reported (docs/PRD.md FR-23, issue #106).
        SeedCollection(harness, CollectionName, group, direct, NeverArchivedMemberId, InvalidMemberEntry, group);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportCollectionAsync(
            CollectionRequest(harness, output), null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { group, direct }, result.ConversationIds);

        var scope = result.Scope!;
        Assert.Equal(3, scope.MemberConversationCount);
        Assert.Equal(2, scope.ArchivedMemberConversationCount);
        Assert.Equal(new[] { NeverArchivedMemberId }, scope.UnarchivedConversationIds);
        Assert.Equal(1, scope.InvalidMemberCount);
        Assert.Equal(1, scope.DuplicateMemberCount);

        using var manifest = ReadManifest(output);
        var manifestScope = manifest.RootElement.GetProperty("scope");
        Assert.Equal("collection", manifestScope.GetProperty("kind").GetString());
        Assert.Equal(CollectionName, manifestScope.GetProperty("collection").GetString());
        Assert.Equal(2, manifestScope.GetProperty("archived_member_conversation_count").GetInt32());
        Assert.Equal(
            new[] { NeverArchivedMemberId },
            manifestScope.GetProperty("unarchived_conversation_ids").EnumerateArray()
                .Select(v => v.GetString()).ToArray());
        Assert.Equal(1, manifestScope.GetProperty("invalid_member_count").GetInt32());
        Assert.Equal(1, manifestScope.GetProperty("duplicate_member_count").GetInt32());
    }

    [Fact]
    public async Task CollectionScopedTimeRangeExportsExactlyTheSelectedRecords()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var direct = DirectStableId(harness.Adapter);
        SeedCollection(harness, CollectionName, group, direct, NeverArchivedMemberId);
        var output = temp.Combine("export");

        // The fixture base instant is 2026-01-15T09:00:00+08:00. An inclusive upper bound at
        // Base+1d keeps the group's record at exactly that instant and drops the +1d5m one; the
        // direct conversation's +3d record falls outside as well (19 + 3 = 22).
        var result = await harness.Workflow.ExportCollectionAsync(
            new ExportCollectionRequest
            {
                CollectionName = CollectionName,
                OutputDirectory = output,
                Since = FixtureSourceAdapter.Base,
                Until = FixtureSourceAdapter.Base.AddDays(1),
            },
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(22, result.RecordCount);

        var groupLines = File.ReadAllLines(
            Path.Combine(output, "chats", "groups", group, "2026", "2026-01.jsonl"), System.Text.Encoding.UTF8);
        Assert.Equal(19, groupLines.Count(line => line.Length > 0));

        var directLines = File.ReadAllLines(
            Path.Combine(output, "chats", "direct", direct, "2026", "2026-01.jsonl"), System.Text.Encoding.UTF8);
        Assert.Equal(3, directLines.Count(line => line.Length > 0));

        // No partition is written for a month outside the requested range.
        Assert.False(File.Exists(Path.Combine(output, "chats", "groups", group, "2026", "2026-02.jsonl")));

        var scope = result.Scope!;
        Assert.Equal(3, scope.MemberConversationCount);
        Assert.Equal(2, scope.ArchivedMemberConversationCount);
        Assert.Equal(new[] { NeverArchivedMemberId }, scope.UnarchivedConversationIds);
        Assert.Equal(
            "2026-01-15T09:00:00+08:00",
            scope.Since);
        Assert.Equal("2026-01-16T09:00:00+08:00", scope.Until);

        using var manifest = ReadManifest(output);
        var manifestScope = manifest.RootElement.GetProperty("scope");
        Assert.Equal("2026-01-15T09:00:00+08:00", manifestScope.GetProperty("since").GetString());
        Assert.Equal("2026-01-16T09:00:00+08:00", manifestScope.GetProperty("until").GetString());
        // The manifest time_range reports the exported records' observed span, not the request.
        var timeRange = manifest.RootElement.GetProperty("time_range");
        Assert.Equal("2026-01-15T09:00:00+08:00", timeRange.GetProperty("first_message_at").GetString());
        Assert.Equal("2026-01-16T09:00:00+08:00", timeRange.GetProperty("last_message_at").GetString());
    }

    [Fact]
    public async Task TimeBoundsAreInclusiveAndTruncateSubSecondComponents()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var output = temp.Combine("export");

        // Inclusive bounds: the records at exactly Base and exactly Base+2h are kept.
        var result = await harness.Workflow.ExportConversationAsync(
            new ExportConversationRequest
            {
                ConversationId = group,
                OutputDirectory = output,
                Since = FixtureSourceAdapter.Base,
                Until = FixtureSourceAdapter.Base.AddHours(2),
            },
            null,
            CancellationToken.None);

        Assert.Equal(8, result.RecordCount);

        // A sub-second upper-bound component is truncated at the archive's one-second resolution,
        // so the record at exactly Base+2h is still inside the range (docs/CLI.md time values).
        var truncated = await harness.Workflow.ExportConversationAsync(
            new ExportConversationRequest
            {
                ConversationId = group,
                OutputDirectory = temp.Combine("export-subsecond"),
                Since = FixtureSourceAdapter.Base,
                Until = FixtureSourceAdapter.Base.AddHours(2).AddMilliseconds(500),
            },
            null,
            CancellationToken.None);

        Assert.Equal(8, truncated.RecordCount);
    }

    [Fact]
    public async Task TimeRangeReExportRemovesStaleMonthPartitions()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var output = temp.Combine("export");

        await harness.Workflow.ExportConversationAsync(
            new ExportConversationRequest { ConversationId = group, OutputDirectory = output },
            null,
            CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", group, "2026", "2026-02.jsonl")));

        // Re-exporting the conversation with a narrower range rewrites its directory, so the
        // stale February partition is structurally impossible afterwards
        // (docs/EXPORT_PRD.md sections 3.2 and 9.4).
        var narrowed = await harness.Workflow.ExportConversationAsync(
            new ExportConversationRequest
            {
                ConversationId = group,
                OutputDirectory = output,
                Until = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(8)),
            },
            null,
            CancellationToken.None);

        Assert.Equal(21, narrowed.RecordCount);
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", group, "2026", "2026-01.jsonl")));
        Assert.False(File.Exists(Path.Combine(output, "chats", "groups", group, "2026", "2026-02.jsonl")));
    }

    [Fact]
    public async Task IdentitiesCoverExactlyTheExportedTimelineSenders()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        SeedCollection(harness, CollectionName, group);
        var output = temp.Combine("export");

        var result = await harness.Workflow.ExportCollectionAsync(
            new ExportCollectionRequest
            {
                CollectionName = CollectionName,
                OutputDirectory = output,
                Since = FixtureSourceAdapter.Base,
                Until = FixtureSourceAdapter.Base.AddHours(2),
            },
            null,
            CancellationToken.None);

        Assert.Equal(8, result.RecordCount);

        // identities.yaml publishes exactly the participants referenced by the exported timelines
        // (docs/EXPORT_PRD.md section 5.2): the three in-range senders, and not the account
        // participant whose only records fall outside the range.
        var identities = await File.ReadAllTextAsync(Path.Combine(output, "identities.yaml"));
        var keys = System.Text.RegularExpressions.Regex.Matches(identities, @"^  (u_[0-9a-f]{16}):", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var expected = new[]
        {
            AliceStableId(harness.Adapter),
            BobStableId(harness.Adapter),
            CarolStableId(harness.Adapter),
        }.OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, keys);
        Assert.DoesNotContain(MeStableId(harness.Adapter), keys);
    }

    [Fact]
    public async Task ManifestDeclaresTheRequestedScopeForBothScopeKinds()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var direct = DirectStableId(harness.Adapter);
        SeedCollection(harness, CollectionName, group, direct);
        var collectionOutput = temp.Combine("export-collection");
        var conversationOutput = temp.Combine("export-conversation");

        await harness.Workflow.ExportCollectionAsync(
            CollectionRequest(harness, collectionOutput), null, CancellationToken.None);
        await harness.Workflow.ExportConversationAsync(
            new ExportConversationRequest { ConversationId = group, OutputDirectory = conversationOutput },
            null,
            CancellationToken.None);

        using var collectionManifest = ReadManifest(collectionOutput);
        var collectionScope = collectionManifest.RootElement.GetProperty("scope");
        Assert.Equal("collection", collectionScope.GetProperty("kind").GetString());
        Assert.Equal(CollectionName, collectionScope.GetProperty("collection").GetString());
        Assert.Equal(2, collectionScope.GetProperty("member_conversation_count").GetInt32());
        Assert.False(collectionScope.TryGetProperty("conversation_id", out _));

        using var conversationManifest = ReadManifest(conversationOutput);
        var conversationScope = conversationManifest.RootElement.GetProperty("scope");
        Assert.Equal("conversation", conversationScope.GetProperty("kind").GetString());
        Assert.Equal(group, conversationScope.GetProperty("conversation_id").GetString());
        Assert.False(conversationScope.TryGetProperty("collection", out _));
        Assert.False(conversationScope.TryGetProperty("member_conversation_count", out _));
        Assert.False(conversationScope.TryGetProperty("unarchived_conversation_ids", out _));
    }

    [Fact]
    public async Task CollectionWithNoArchivedMemberFailsDeterministically()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, NeverArchivedMemberId);

        var exception = await Assert.ThrowsAsync<ExportScopeException>(() =>
            harness.Workflow.ExportCollectionAsync(
                CollectionRequest(harness, temp.Combine("export")), null, CancellationToken.None));

        // The failure names the missing members instead of publishing an unattributable package.
        Assert.Contains(CollectionName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(NeverArchivedMemberId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchivedMembersFromDifferentAccountsFailDeterministically()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);

        const string secondAccountId = "a_00000000000000f2";
        const string secondConversationId = "g_00000000000000f2";
        await harness.Store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = secondAccountId,
            SourceProfileId = "other_account",
            AdapterName = "fixture",
            AdapterVersion = "1.0.0",
            SourceVersion = "fixture-1",
        }, CancellationToken.None);
        await harness.Store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = secondConversationId,
                AccountId = secondAccountId,
                SourceConversationId = "other-room",
                Kind = ConversationKind.Group,
                Title = "其他群",
            },
        ], CancellationToken.None);

        // One package carries exactly one manifest source_account_id, so archived members that
        // span accounts cannot be truthfully described by one export (docs/PRD.md FR-23).
        SeedCollection(harness, CollectionName, group, secondConversationId);

        var exception = await Assert.ThrowsAsync<ExportScopeException>(() =>
            harness.Workflow.ExportCollectionAsync(
                CollectionRequest(harness, temp.Combine("export")), null, CancellationToken.None));

        Assert.Contains(secondAccountId, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaughtFailureDuringCollectionReExportRestoresThePriorPackage()
    {
        using var temp = new TempDirectory();
        var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter), DirectStableId(harness.Adapter));
        var output = temp.Combine("export");

        await harness.Workflow.ExportCollectionAsync(
            CollectionRequest(harness, output), null, CancellationToken.None);
        var before = Snapshot(output);

        // A caught failure during the second (R1) export must restore the prior package
        // in-process; export reliability semantics do not change with the scope (issue #106).
        var clock = new FixedClock();
        var faultingWorkflow = new ArchiveWorkflow(
            new FaultingJsonlDatasetExporter(harness.Store),
            harness.Store,
            new CollectionCatalogService(harness.CollectionSource),
            clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faultingWorkflow.ExportCollectionAsync(
                CollectionRequest(harness, output), null, CancellationToken.None));

        var after = Snapshot(output);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var (file, content) in before)
        {
            Assert.Equal(content, after[file]);
        }
    }

    private static Dictionary<string, string> Snapshot(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            result[relative] = File.ReadAllText(file);
        }

        return result;
    }

    /// <summary>
    /// Throws on the second <see cref="JsonlDatasetExporter.DurableCommit"/> call to fault-inject a
    /// caught commit failure after the first member's replacement, proving the in-process rollback
    /// restores the prior package (the same pattern the single-conversation R1 tests use).
    /// </summary>
    private sealed class FaultingJsonlDatasetExporter(IArchiveStore archive) : JsonlDatasetExporter(archive)
    {
        private int _commits;

        protected internal override void DurableCommit(string directory)
        {
            if (++_commits == 2)
            {
                throw new InvalidOperationException("simulated commit checkpoint failure");
            }
        }
    }
}
