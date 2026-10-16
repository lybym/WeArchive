using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for Collection-scoped export and time-range selection
/// (issue #106, docs/CLI.md <c>export</c>): option parsing, scope mutual exclusion, the documented
/// time grammar, Collection resolution error mapping, the pinned JSON scope block and the default
/// destination. Commands run through <see cref="CliHost.RunAsync"/> against a temporary SQLite
/// archive, so the CLI is measured only as a transport adapter.
/// </summary>
public sealed class ExportCollectionCliTests
{
    private const string CollectionName = "ai-toy";

    /// <summary>A well-formed stable conversation id that no fixture test ever archives.</summary>
    private const string NeverArchivedMemberId = "u_000000000000beef";

    private sealed class Harness : IDisposable
    {
        public required FixtureSourceAdapter Adapter { get; init; }
        public required StubCollectionCatalogSource CollectionSource { get; init; }
        public required ServiceProvider Provider { get; init; }

        public void Dispose() => Provider.Dispose();
    }

    private static async Task<Harness> CreateHarnessAsync(TempDirectory temp)
    {
        var fixture = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var archivePath = temp.Combine("archive.db");

        // Seed the canonical archive through the publication engine — the same engine an explicit
        // `sync` drives — so every export assertion measures the canonical-archive-only boundary.
        var seedStore = new SqliteArchiveStore(archivePath, clock);
        var importer = new ImportService(new FixtureSourceAdapter(), seedStore, clock);
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

        var collectionSource = new StubCollectionCatalogSource();
        var services = new ServiceCollection();
        // Pre-register the stub Collection catalog so AddWeArchiveCore's TryAddSingleton keeps it
        // (the same pattern the query CLI tests use).
        services.AddSingleton<ICollectionCatalogSource>(collectionSource);
        services.AddWeArchiveCore(archivePath, temp.Combine("rawvault"));
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISourceAdapter>(fixture);
        services.AddSingleton<ISourceCaptureAdapter, FixtureCaptureAdapter>();
        services.AddSingleton(new CliExportDefaults { DefaultOutputDirectory = temp.Combine("export") });
        var provider = services.BuildServiceProvider();

        return new Harness { Adapter = fixture, CollectionSource = collectionSource, Provider = provider };
    }

    private static string GroupStableId(FixtureSourceAdapter adapter) =>
        StableIds.GroupConversation(
            StableIds.Account(adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId),
            FixtureSourceAdapter.GroupConversation);

    private static string DirectStableId(FixtureSourceAdapter adapter) =>
        StableIds.Conversation(
            StableIds.Account(adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId),
            ConversationKind.Direct,
            FixtureSourceAdapter.DirectConversation,
            FixtureSourceAdapter.Alice);

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

    private static Task<int> RunAsync(
        ServiceProvider provider, string[] args,
        TextWriter stdout, TextWriter stderr,
        CancellationToken ct = default) =>
        CliHost.RunAsync(args, stdout, stderr, provider, ct);

    private static JsonDocument ParseSingleJson(StringWriter stdout)
    {
        var output = stdout.ToString().TrimEnd();
        Assert.NotEmpty(output);
        Assert.False(output.Contains('\n'), "stdout must contain exactly one JSON document");
        return JsonDocument.Parse(output);
    }

    // ---- collection scope: JSON success --------------------------------------

    [Fact]
    public async Task ExportCollectionJsonPublishesTheScopeAndExitsZero()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var group = GroupStableId(harness.Adapter);
        var direct = DirectStableId(harness.Adapter);
        SeedCollection(harness, CollectionName, group, direct, NeverArchivedMemberId, "wxid_typo", group);
        var output = temp.Combine("out");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName, "--output", output, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = ParseSingleJson(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal(output, root.GetProperty("output_directory").GetString());
        Assert.Equal(new[] { group, direct }, root.GetProperty("conversation_ids")
            .EnumerateArray().Select(v => v.GetString()).ToArray());

        var scope = root.GetProperty("scope");
        Assert.Equal("collection", scope.GetProperty("kind").GetString());
        Assert.Equal(CollectionName, scope.GetProperty("collection").GetString());
        Assert.Equal(3, scope.GetProperty("member_conversation_count").GetInt32());
        Assert.Equal(2, scope.GetProperty("archived_member_conversation_count").GetInt32());
        Assert.Equal(new[] { NeverArchivedMemberId }, scope.GetProperty("unarchived_conversation_ids")
            .EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal(1, scope.GetProperty("invalid_member_count").GetInt32());
        Assert.Equal(1, scope.GetProperty("duplicate_member_count").GetInt32());

        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(output, "chats", "groups", group)));

        // No progress pollution on stdout in JSON mode.
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task ExportCollectionHumanOutputNamesTheScopeAndUnarchivedMembers()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter), NeverArchivedMemberId);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName, "--output", temp.Combine("out")],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);
        var content = stdout.ToString();
        Assert.Contains("Exported", content);
        Assert.Contains($"collection {CollectionName} (1/2 members archived", content);
        Assert.Contains(NeverArchivedMemberId, content);
        Assert.Contains("Resolving collection", stderr.ToString());
        Assert.DoesNotContain("Resolving collection", content);
        Assert.DoesNotContain("{", content);
    }

    [Fact]
    public async Task ExportCollectionDefaultsToACollectionNamedRoot()
    {
        // The documented default when --output is omitted: a distinct collection-<name> root under
        // the host exports directory, which can never collide with a stable-id conversation root.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter));
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);

        var expectedRoot = Path.Combine(temp.Combine("export"), $"collection-{CollectionName}");
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(expectedRoot, doc.RootElement.GetProperty("output_directory").GetString());
        Assert.True(File.Exists(Path.Combine(expectedRoot, "manifest.json")));
    }

    // ---- collection scope: deterministic failures -----------------------------

    [Fact]
    public async Task ExportCollectionNotFoundExitsOne()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        // No configuration at all: an empty catalog, so the name is deterministically unknown.
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", "no-such-collection", "--output", temp.Combine("out"), "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal("collection_not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportCollectionConfigInvalidExitsTwo()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        // The documented shape requires the top-level collections mapping; its absence is a
        // configuration validation failure, exactly as every other Collection-scoped command maps.
        harness.CollectionSource.Document = new CollectionCatalogDocument { SchemaVersion = "1.0" };
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName, "--output", temp.Combine("out"), "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal("collection_config_invalid", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportCollectionWithNoArchivedMemberExitsOneWithoutPublishingAPackage()
    {
        // A scope whose members the archive does not know has no dataset to derive and no account
        // to name in the manifest, so it fails deterministically instead of publishing a package
        // that reads as complete (docs/CLI.md `export`).
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, NeverArchivedMemberId);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName, "--output", output, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var failureDoc = ParseSingleJson(stdout);
        var error = failureDoc.RootElement.GetProperty("error");
        Assert.Equal("failure", error.GetProperty("code").GetString());
        Assert.Contains(NeverArchivedMemberId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    // ---- usage contract --------------------------------------------------------

    [Fact]
    public async Task ExportRejectsSupplyingBothScopes()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--collection", CollectionName, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal("usage_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportRejectsSupplyingNeitherScope()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--output", temp.Combine("out"), "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal("usage_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportRejectsAnUnparseableSinceValue()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter));
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName, "--since", "not-a-date", "--output", temp.Combine("out"), "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal("usage_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(temp.Combine("out")));
    }

    [Fact]
    public async Task ExportRejectsSinceLaterThanUntil()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter));
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName,
             "--since", "2026-02-01", "--until", "2026-01-01", "--output", temp.Combine("out"), "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal("usage_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(temp.Combine("out")));
    }

    // ---- time-range selection ---------------------------------------------------

    [Fact]
    public async Task ExportCollectionTimeRangeNarrowsTheDataset()
    {
        // The fixture base instant is 2026-01-15T09:00:00+08:00 and the fixed clock offset is
        // +08:00. Date-only bounds are that local offset's midnight: the range keeps the group's
        // 18 January records before January 16 midnight and the direct conversation's 3, and the
        // record at 2026-01-16T09:00:00+08:00 falls outside (docs/CLI.md time values).
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        SeedCollection(harness, CollectionName, GroupStableId(harness.Adapter), DirectStableId(harness.Adapter));
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--collection", CollectionName,
             "--since", "2026-01-15", "--until", "2026-01-16", "--output", output, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(21, doc.RootElement.GetProperty("record_count").GetInt32());
        Assert.Equal(
            "2026-01-15T00:00:00+08:00",
            doc.RootElement.GetProperty("scope").GetProperty("since").GetString());
        Assert.Equal(
            "2026-01-16T00:00:00+08:00",
            doc.RootElement.GetProperty("scope").GetProperty("until").GetString());
    }

    [Fact]
    public async Task ExportConversationTimeRangeNarrowsTheDataset()
    {
        // The time-range refinement applies to the conversation scope too (FR-18): only the
        // fixture group's two February records are inside the range.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation,
             "--since", "2026-02-01", "--output", output, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(2, doc.RootElement.GetProperty("record_count").GetInt32());
        var scope = doc.RootElement.GetProperty("scope");
        Assert.Equal("conversation", scope.GetProperty("kind").GetString());
        Assert.Equal(
            "2026-02-01T00:00:00+08:00",
            scope.GetProperty("since").GetString());
        Assert.Equal(GroupStableId(harness.Adapter), scope.GetProperty("conversation_id").GetString());
    }
}
