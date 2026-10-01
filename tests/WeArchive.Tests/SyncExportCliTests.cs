using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI integration + reliability-semantics tests for <c>wearchive sync</c> resolution/usage and
/// <c>wearchive export</c>. Commands are exercised through <see cref="CliHost.RunAsync"/>
/// against a temporary SQLite archive and the fixture source, so import/export semantics
/// stay in Core/Infrastructure and the CLI is measured only as a transport adapter.
/// <c>sync --conversation</c> publication is covered by <c>ConversationSyncCliTests</c>, which
/// runs the preservation-first capture -&gt; Raw Vault -&gt; ingest path over the synthetic WeChat
/// source (Issue #49).
/// docs/PRD.md FR-04/FR-08/FR-09/FR-12/FR-14/FR-22, docs/DEVELOPMENT.md sections 7 and 10
/// (Reliability Levels: Import R2, Export R1).
/// </summary>
public sealed class SyncExportCliTests
{
    private sealed class Harness : IDisposable
    {
        public required FixtureSourceAdapter Adapter { get; init; }
        public required SqliteArchiveStore Store { get; init; }
        public required ServiceProvider Provider { get; init; }
        public required string ArchivePath { get; init; }
        public required IClock Clock { get; init; }

        public void Dispose() => Provider.Dispose();
    }

    private static async Task<Harness> CreateHarnessAsync(
        TempDirectory temp,
        ISourceAdapter? adapter = null,
        Func<IArchiveStore, IDatasetExporter>? exporterFactory = null)
    {
        var fixture = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var archivePath = temp.Combine("archive.db");

        // Seed the canonical archive through the publication engine — the same engine an explicit
        // `sync` drives — so every export assertion measures the canonical-archive-only boundary
        // (Issue #66) instead of an implicit live-source re-import. Seeding always uses the fixture
        // source, so a test may still inject a source that throws or counts calls.
        var seedStore = new SqliteArchiveStore(archivePath, clock);
        var importer = new ImportService(new FixtureSourceAdapter(), seedStore, clock);
        await importer.ImportConversationAsync(new ImportRequest
        {
            SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
            SourceConversationId = FixtureSourceAdapter.GroupConversation,
            Kind = ConversationKind.Group,
            ConversationTitle = "华东产品创新中心工作群",
        }, null, CancellationToken.None).ConfigureAwait(false);
        await importer.ImportConversationAsync(new ImportRequest
        {
            SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
            SourceConversationId = FixtureSourceAdapter.DirectConversation,
            Kind = ConversationKind.Direct,
            PeerSourceUserId = FixtureSourceAdapter.Alice,
            ConversationTitle = "张三",
        }, null, CancellationToken.None).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddWeArchiveCore(archivePath, temp.Combine("rawvault"));
        // AddWeArchiveCore uses TryAddSingleton; later explicit registrations win on
        // resolution, so override the clock and (when provided) the adapter/exporter.
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISourceAdapter>(adapter ?? fixture);
        services.AddSingleton<ISourceCaptureAdapter, FixtureCaptureAdapter>();
        if (exporterFactory is not null)
        {
            services.AddSingleton<IDatasetExporter>(sp => exporterFactory(sp.GetRequiredService<IArchiveStore>()));
        }

        services.AddSingleton(new CliExportDefaults { DefaultOutputDirectory = temp.Combine("export") });
        var provider = services.BuildServiceProvider();

        return new Harness
        {
            Adapter = fixture,
            Store = (SqliteArchiveStore)provider.GetRequiredService<IArchiveStore>(),
            Provider = provider,
            ArchivePath = archivePath,
            Clock = clock,
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
        Assert.False(output.Contains('\u001b'), "stdout must not carry ANSI escape decoration");
        return JsonDocument.Parse(output);
    }

    private static string GroupStableId(FixtureSourceAdapter adapter) =>
        StableIds.GroupConversation(
            StableIds.Account(adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId),
            FixtureSourceAdapter.GroupConversation);


    // ---- sync: resolution and usage contract --------------------------------
    //
    // `sync --conversation` publication runs the preservation-first capture -> Raw Vault -> ingest
    // path and is covered by ConversationSyncCliTests over the synthetic WeChat source (Issue #49).
    // The cases here never reach publication: they pin selector resolution error codes and the
    // usage contract, which are independent of the publication path.

    [Fact]
    public async Task SyncMissingConversationFlagExitsTwo()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider, ["sync", "--json"], stdout, stderr);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.UsageError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncUnknownOptionExitsTwo()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider, ["sync", "--bogus", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.UsageError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }



    [Fact]
    public async Task SyncNoAccountsEmitsNoAccountsCode()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp, new ResolutionFailureSource { NoAccounts = true });
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", "any", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.NoAccounts, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncSourceUnavailableEmitsSourceUnavailableCode()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp, new ResolutionFailureSource { AccountListingFails = true });
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", "any", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.SourceUnavailable, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncConversationNotFoundEmitsGranularCodeAndExitsOne()
    {
        // Resolution failures must surface the documented granular code, not a generic
        // failure, so the machine contract is consistent with `conversation show`
        // (docs/CLI.md failure-document table).
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", "does-not-exist", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.ConversationNotFound, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }




    // ---- export: happy path, documented package, JSON ----

    [Fact]
    public async Task ExportSuccessPublishesTheDocumentedPackageAndExitsZero()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            new StringWriter(), TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);

        // docs/EXPORT_PRD.md section 3 canonical package layout.
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "identities.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "conversations.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "collections.yaml")));

        var conversationId = GroupStableId(harness.Adapter);
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", conversationId, "2026", "2026-01.jsonl")));
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", conversationId, "2026", "2026-02.jsonl")));
    }

    [Fact]
    public async Task ExportJsonEmitsOutputLocationCountersAndDiagnostics()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = ParseSingleJson(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("succeeded").GetBoolean());
        Assert.Equal(output, root.GetProperty("output_directory").GetString());
        Assert.Equal(GroupStableId(harness.Adapter), root.GetProperty("conversation_ids")[0].GetString());
        Assert.True(root.GetProperty("record_count").GetInt32() > 0);
        Assert.True(root.GetProperty("unknown_count").GetInt32() >= 1);

        var files = root.GetProperty("files").EnumerateArray().ToList();
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "timeline");
        Assert.Contains(files, f => f.GetProperty("kind").GetString() == "identities");

        var paths = root.GetProperty("conversation_paths").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(paths, p => p!.StartsWith("chats/groups/", StringComparison.Ordinal));

        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.Contains(diagnostics, d => d.GetProperty("code").GetString() == "unknown_message_type");

        // No progress pollution on stdout in JSON mode.
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task ExportHumanWritesResultToStdoutAndProgressToStderr()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);
        var stdoutContent = stdout.ToString();
        Assert.Contains("Exported", stdoutContent);
        Assert.Contains("Resolving", stderr.ToString());
        Assert.DoesNotContain("Resolving", stdoutContent);
        Assert.DoesNotContain("{", stdoutContent);
    }

    [Fact]
    public async Task ExportUsesTheArchiveStableIdMechanism()
    {
        // Export resolves the selector from the canonical archive only (Issue #66): the upstream
        // source conversation id is accepted because it resolves uniquely among archived
        // conversations, and the dataset is keyed by the same stable archive id the importer
        // derives — no second alias/id store is introduced by the CLI.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            stdout, TextWriter.Null);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(GroupStableId(harness.Adapter), doc.RootElement.GetProperty("conversation_ids")[0].GetString());
    }

    [Fact]
    public async Task ExportResolvesByStableArchiveId()
    {
        // `--conversation` must accept the canonical stable archive id (g_/u_), not only the
        // upstream source id, mirroring `conversation show` and `sync`.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        var stableId = GroupStableId(harness.Adapter);

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", stableId, "--output", output, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(stableId, doc.RootElement.GetProperty("conversation_ids")[0].GetString());
    }

    [Fact]
    public async Task ExportDefaultsToAPerConversationRootUnderTheHostExportsDirectory()
    {
        // The documented default when --output is omitted: a stable-id subdirectory of the
        // host exports directory, so the default destination is a self-consistent
        // single-conversation package (docs/CLI.md "export").
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);

        var stableId = GroupStableId(harness.Adapter);
        var expectedRoot = Path.Combine(temp.Combine("export"), stableId);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(expectedRoot, doc.RootElement.GetProperty("output_directory").GetString());

        Assert.True(File.Exists(Path.Combine(expectedRoot, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(expectedRoot, "conversations.yaml")));
        Assert.True(File.Exists(Path.Combine(expectedRoot, "chats", "groups", stableId, "2026", "2026-01.jsonl")));
    }

    [Fact]
    public async Task ExportShortOutputOptionFormWritesToTheRequestedRoot()
    {
        // docs/CLI.md documents `-o, --output <dir>`; the short form must not be a dead option.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("short");
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "-o", output, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(output, doc.RootElement.GetProperty("output_directory").GetString());
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
    }

    [Fact]
    public async Task ExportDefaultsKeepDifferentConversationsInSeparateSelfConsistentRoots()
    {
        // A regression guard for the shared-root hazard: --output is a single-conversation
        // package root, so exporting two conversations must not de-index the first. The
        // documented default gives each conversation its own stable-id root, and each root's
        // manifest must still describe exactly its own conversation.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var exportsRoot = temp.Combine("export");

        var firstExit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            new StringWriter(), TextWriter.Null);
        Assert.Equal(ExitCode.Success, firstExit);

        var secondExit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.DirectConversation, "--json"],
            new StringWriter(), TextWriter.Null);
        Assert.Equal(ExitCode.Success, secondExit);

        var adapter = harness.Adapter;
        var accountId = StableIds.Account(adapter.AdapterName, FixtureSourceAdapter.FixtureAccountId);
        var groupId = StableIds.Conversation(
            accountId, ConversationKind.Group, FixtureSourceAdapter.GroupConversation, null);
        var directId = StableIds.Conversation(
            accountId, ConversationKind.Direct, FixtureSourceAdapter.DirectConversation, FixtureSourceAdapter.Alice);

        Assert.NotEqual(groupId, directId);

        var groupManifest = await ReadManifestAsync(Path.Combine(exportsRoot, groupId));
        var directManifest = await ReadManifestAsync(Path.Combine(exportsRoot, directId));

        // The group was exported first; the later direct-conversation default export must not
        // have touched it.
        Assert.Equal([groupId], ManifestIds(groupManifest, "conversations", "conversation_id"));
        Assert.Equal([directId], ManifestIds(directManifest, "conversations", "conversation_id"));
        Assert.Equal([groupId], ManifestIds(groupManifest, "conversation_ids", null));
        Assert.Equal([directId], ManifestIds(directManifest, "conversation_ids", null));

        // Each root still carries its own timeline partition.
        Assert.True(File.Exists(Path.Combine(exportsRoot, groupId, "chats", "groups", groupId, "2026", "2026-01.jsonl")));
        Assert.True(File.Exists(Path.Combine(exportsRoot, directId, "chats", "direct", directId, "2026", "2026-01.jsonl")));
    }

    [Fact]
    public async Task ExportReportsFailureWhenTheExporterReturnsNotSucceeded()
    {
        // ExportResult is a Core contract that may report Succeeded=false without throwing.
        // That must never surface as process success with a result document: the CLI maps it to
        // exit 1 plus the documented failure document carrying the exporter's reason.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp, exporterFactory: _ => new FailingExporter());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Failure, exit);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Failure, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("disk full", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.False(doc.RootElement.TryGetProperty("succeeded", out _));

        Assert.Contains("disk full", stderr.ToString());
    }

    private static async Task<string> ReadManifestAsync(string root) =>
        await File.ReadAllTextAsync(Path.Combine(root, "manifest.json")).ConfigureAwait(false);

    /// <summary>
    /// The conversation ids of a manifest, read either from the top-level
    /// <c>conversation_ids</c> array or from the <c>conversations[]</c> entries.
    /// </summary>
    private static IReadOnlyList<string?> ManifestIds(string manifestJson, string property, string? itemProperty)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var element = doc.RootElement.GetProperty(property);
        return
        [
            .. element.EnumerateArray().Select(item =>
                itemProperty is null ? item.GetString() : item.GetProperty(itemProperty).GetString()),
        ];
    }

    // ---- export: R1 caught failure restores prior output, exit 1 ----

    [Fact]
    public async Task ExportCaughtFailureRestoresPriorOutputAndExitsOne()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp, exporterFactory: store => new FaultingExporter(store));
        var faulting = (FaultingExporter)harness.Provider.GetRequiredService<IDatasetExporter>();
        var output = temp.Combine("out");

        // Establish a complete prior package. The faulting exporter only fails on the next
        // commit, so the first export succeeds.
        var first = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            new StringWriter(), TextWriter.Null);
        Assert.Equal(ExitCode.Success, first);

        var conversationId = GroupStableId(harness.Adapter);
        var priorManifest = await File.ReadAllTextAsync(Path.Combine(output, "manifest.json"));
        var priorTimeline = await File.ReadAllTextAsync(
            Path.Combine(output, "chats", "groups", conversationId, "2026", "2026-01.jsonl"));

        // A second export faults at the conversation commit checkpoint. R1: the exporter
        // restores the prior package in-process; the CLI surfaces it as exit 1.
        faulting.FailNextCommit = true;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Failure, doc.RootElement.GetProperty("error").GetProperty("code").GetString());

        // The prior package survives untouched — no partial output is treated as complete.
        Assert.Equal(priorManifest, await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        Assert.Equal(priorTimeline, await File.ReadAllTextAsync(
            Path.Combine(output, "chats", "groups", conversationId, "2026", "2026-01.jsonl")));

        var groupsBucket = Path.Combine(output, "chats", "groups");
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(groupsBucket),
            d => Path.GetFileName(d).Contains(".wearchive-backup-", StringComparison.Ordinal)
              || Path.GetFileName(d).StartsWith("wearchive-export-staging-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExportCancellationExits130()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            stdout, TextWriter.Null, cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Cancelled, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportMissingConversationFlagExitsTwo()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider, ["export", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.UsageError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportUnknownConversationFailsWithoutSourceAccess()
    {
        // Export is a canonical-archive-only derivation (Issue #66): an unknown conversation is the
        // documented conversation-not-found operation failure, and no package may be produced. The
        // injected source throws on every method, so the failure also proves export never consults
        // the live source for a selector the archive cannot resolve.
        using var temp = new TempDirectory();
        var source = new ThrowingSource();
        using var harness = await CreateHarnessAsync(temp, source);
        var output = temp.Combine("out");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", "g_0000000000000000", "--output", output, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Failure, exit);
        Assert.Equal(0, source.CallCount);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.ConversationNotFound, doc.RootElement.GetProperty("error").GetProperty("code").GetString());

        // No dataset package may be produced for a conversation the archive does not hold.
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ExportIsIdempotentAcrossSuccessfulRuns()
    {
        // docs/EXPORT_PRD.md section 15 / docs/CLI.md "export": given the same archive state and
        // exporter version, two successful exports are byte-identical except for the explicitly
        // generated metadata. This is asserted with the clock *advanced between the runs* — the
        // shipped SystemClock is real wall time, so a frozen clock would make the claim
        // unfalsifiable. Every file other than manifest.json must be byte-identical, and inside
        // manifest.json `created_at` must be the only field that differs.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");

        var first = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            new StringWriter(), TextWriter.Null);
        Assert.Equal(ExitCode.Success, first);

        var snapshot = SnapshotExportFiles(output);
        var firstManifest = System.Text.Encoding.UTF8.GetString(snapshot["manifest.json"]);

        // Advance the clock so the export timestamp genuinely changes, as it does in production.
        ((FixedClock)harness.Clock).UtcNow = harness.Clock.UtcNow.AddHours(3);

        var second = await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            new StringWriter(), TextWriter.Null);
        Assert.Equal(ExitCode.Success, second);

        var after = SnapshotExportFiles(output);
        Assert.Equal(snapshot.Count, after.Count);
        foreach (var (relativePath, bytes) in snapshot)
        {
            Assert.True(after.TryGetValue(relativePath, out var afterBytes),
                $"file disappeared on re-export: {relativePath}");

            if (relativePath == "manifest.json")
            {
                continue;
            }

            Assert.True(bytes.SequenceEqual(afterBytes),
                $"file content changed on re-export: {relativePath}");
        }

        // The re-export regenerated nothing but the generated metadata: `created_at` is the only
        // differing manifest field.
        var secondManifest = System.Text.Encoding.UTF8.GetString(after["manifest.json"]);
        Assert.NotEqual(firstManifest, secondManifest);
        Assert.Equal(
            ["created_at"],
            DifferingManifestFields(firstManifest, secondManifest));
    }

    private static Dictionary<string, byte[]> SnapshotExportFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), File.ReadAllBytes);

    /// <summary>
    /// The names of the top-level manifest fields whose values differ between two exports.
    /// </summary>
    private static IReadOnlyList<string> DifferingManifestFields(string firstJson, string secondJson)
    {
        using var first = JsonDocument.Parse(firstJson);
        using var second = JsonDocument.Parse(secondJson);

        var differing = new List<string>();
        foreach (var property in first.RootElement.EnumerateObject())
        {
            var other = second.RootElement.GetProperty(property.Name);
            if (!string.Equals(property.Value.GetRawText(), other.GetRawText(), StringComparison.Ordinal))
            {
                differing.Add(property.Name);
            }
        }

        return differing;
    }

    // ---- no R3+ recovery machinery ----

    [Fact]
    public async Task NoPersistentRecoveryStateIsCreatedByExport()
    {
        // The hard-stop rule forbids journals, commit markers, recovery ledgers or cross-file
        // transaction protocols beyond SQLite's own documented boundary. This test asserts
        // that after a successful import+export, only SQLite's own files and the documented
        // export package remain — no new persistent recovery state.
        //
        // The same assertion for the preservation-first sync path (capture + Raw Vault + ingest)
        // lives in ConversationSyncTests, which owns that path's archive and Raw Vault layout.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var output = temp.Combine("out");

        await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            new StringWriter(), TextWriter.Null);

        // The archive directory contains only SQLite's own files.
        var archiveDir = Path.GetDirectoryName(harness.ArchivePath)!;
        foreach (var file in Directory.EnumerateFiles(archiveDir))
        {
            var name = Path.GetFileName(file);
            Assert.True(
                name.EndsWith(".db", StringComparison.Ordinal)
                || name.EndsWith(".db-wal", StringComparison.Ordinal)
                || name.EndsWith(".db-shm", StringComparison.Ordinal),
                $"unexpected archive file '{name}' — no application recovery state is permitted");
            Assert.False(name.Contains("journal", StringComparison.OrdinalIgnoreCase)
                || name.Contains("commit-marker", StringComparison.OrdinalIgnoreCase)
                || name.Contains("recovery", StringComparison.OrdinalIgnoreCase),
                $"archive file '{name}' looks like a recovery artifact");
        }

        // The export directory carries only the documented package; no exporter staging,
        // backup, root-backup or transaction journal remains after a successful publish.
        foreach (var entry in Directory.EnumerateFileSystemEntries(output, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(entry);
            Assert.False(name.Contains("wearchive-export-staging", StringComparison.Ordinal), $"leftover staging '{name}'");
            Assert.False(name.Contains("wearchive-backup", StringComparison.Ordinal), $"leftover backup '{name}'");
            Assert.False(name.Contains("wearchive-rootbackup", StringComparison.Ordinal), $"leftover root backup '{name}'");
            Assert.False(name.Contains("wearchive-transaction", StringComparison.OrdinalIgnoreCase), $"transaction journal '{name}'");
            Assert.False(name.Contains("commit-marker", StringComparison.OrdinalIgnoreCase), $"commit marker '{name}'");
            Assert.False(name.EndsWith(".journal", StringComparison.OrdinalIgnoreCase), $"journal file '{name}'");
        }
    }

    // ---- help surface includes the new commands ----

    [Fact]
    public async Task HelpListsSyncAndExportCommands()
    {
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var stdout = new StringWriter();

        await RunAsync(harness.Provider, ["--help", "--json"], stdout, TextWriter.Null);

        using var doc = ParseSingleJson(stdout);
        var commands = doc.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("sync", commands);
        Assert.Contains("export", commands);
    }

    // ---- help description stays in sync with command Description properties ----

    [Fact]
    public async Task CommandRouterDescriptionsMatchCommandDescriptions()
    {
        // After the CommandEntry refactor each command's one-line description lives in two
        // places: the CommandRouter registration string and the command's Description property.
        // If they drift, --help (rendered from the router) would differ from a direct
        // command.Description read. This guard keeps them in sync.
        using var temp = new TempDirectory();
        using var harness = await CreateHarnessAsync(temp);
        var sp = harness.Provider;

        var router = new CommandRouter(sp);
        var registered = router.DescribeHelp().Commands
            .ToDictionary(c => c.Name, c => c.Description);

        ICliCommand[] commands =
        [
            new VersionCommand(),
            new DoctorCommand(sp.GetRequiredService<ISourceAdapter>(), sp.GetRequiredService<IArchiveStore>()),
            new AccountCommand(sp.GetRequiredService<SourceCatalogService>()),
            new ConversationCommand(sp.GetRequiredService<SourceCatalogService>()),
            new SyncCommand(
                sp.GetRequiredService<SourceCatalogService>(),
                sp.GetRequiredService<ConversationSyncService>(),
                sp.GetRequiredService<CollectionSyncService>()),
            new ExportCommand(
                sp.GetRequiredService<ArchiveConversationResolver>(),
                sp.GetRequiredService<ArchiveWorkflow>(),
                sp.GetRequiredService<CliExportDefaults>()),
        ];

        foreach (var command in commands)
        {
            Assert.True(registered.TryGetValue(command.Name, out var description),
                $"no registration for command '{command.Name}'");
            Assert.Equal(command.Description, description);
        }
    }

    // ---- stubs ----

    /// <summary>
    /// A source adapter that throws on every method and counts the calls it received. Export must
    /// never reach it: the canonical-archive-only boundary proves source isolation by succeeding
    /// (or failing deterministically) while the call count stays zero (Issue #66).
    /// </summary>
    private sealed class ThrowingSource : ISourceAdapter
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public string AdapterName => "throwing";

        public string AdapterVersion => "1.0.0";

        private T Fail<T>()
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("the source adapter must not be reached by export");
        }

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            Fail<Task<SourceDescriptor>>();

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            Fail<Task<IReadOnlyList<SourceAccount>>>();

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Fail<Task<IReadOnlyList<SourceConversation>>>();

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            Fail<Task<SourceConversationDetail>>();

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Fail<Task<IReadOnlyList<SourceParticipant>>>();

        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            Fail<IAsyncEnumerable<SourceMessage>>();
    }

    /// <summary>
    /// Faults on the next conversation commit checkpoint so a re-export fails after the
    /// replacement move, proving the exporter's in-process rollback (R1) restores the prior
    /// package. Mirrors <c>FaultingJsonlDatasetExporter</c> in ExportPipelineTests.
    /// </summary>
    private sealed class FaultingExporter(IArchiveStore archive) : JsonlDatasetExporter(archive)
    {
        public bool FailNextCommit { get; set; }

        protected internal override void DurableCommit(string directory)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new InvalidOperationException("simulated commit checkpoint failure");
            }

            base.DurableCommit(directory);
        }
    }

    /// <summary>
    /// A source whose account listing can be configured to fail or be empty, so the granular
    /// resolution error codes (<c>no_accounts</c>, <c>source_unavailable</c>) are exercised
    /// through the CLI rather than collapsed to a generic failure.
    /// </summary>
    private sealed class ResolutionFailureSource : ISourceAdapter
    {
        public bool NoAccounts { get; init; }
        public bool AccountListingFails { get; init; }

        public string AdapterName => "resolution";
        public string AdapterVersion => "1.0.0";

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                SourceVersion = "resolution-1",
                SourceProductName = "resolution source",
                IsAvailable = true,
            });

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            if (AccountListingFails)
                throw new InvalidOperationException("source listing failed");
            return Task.FromResult<IReadOnlyList<SourceAccount>>(
                NoAccounts ? [] : [new SourceAccount { SourceProfileId = "rf_account", IsCurrent = true }]);
        }

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceConversation>>([]);

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            Task.FromResult(new SourceConversationDetail { SourceConversationId = sourceConversationId });

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceParticipant>>([]);

        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// An <see cref="IDatasetExporter"/> that reports <c>Succeeded = false</c> without throwing,
    /// exercising the CLI's handling of the Core contract's failure flag (a false success would
    /// otherwise be reported as exit 0).
    /// </summary>
    private sealed class FailingExporter : IDatasetExporter
    {
        public string ExporterVersion => "0.0.0-test";

        public Task<ExportResult> ExportAsync(
            ExportRequest request,
            IProgress<ExportProgress>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ExportResult
            {
                Succeeded = false,
                FailureReason = "disk full",
                OutputDirectory = request.OutputDirectory,
            });
    }

}
