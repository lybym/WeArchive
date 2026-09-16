using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI integration + reliability-semantics tests for <c>wearchive sync</c> and
/// <c>wearchive export</c>. Commands are exercised through <see cref="CliHost.RunAsync"/>
/// against a temporary SQLite archive and the fixture source, so import/export semantics
/// stay in Core/Infrastructure and the CLI is measured only as a transport adapter.
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

    private static Harness CreateHarness(
        TempDirectory temp,
        ISourceAdapter? adapter = null,
        Func<IArchiveStore, IDatasetExporter>? exporterFactory = null)
    {
        var fixture = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var archivePath = temp.Combine("archive.db");

        var services = new ServiceCollection();
        services.AddWeArchiveCore(archivePath);
        // AddWeArchiveCore uses TryAddSingleton; later explicit registrations win on
        // resolution, so override the clock and (when provided) the adapter/exporter.
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISourceAdapter>(adapter ?? fixture);
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

    // ---- sync: happy path, human + JSON, archive state ----

    [Fact]
    public async Task SyncSuccessPublishesToArchiveAndExitsZero()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);

        var conversationId = GroupStableId(harness.Adapter);
        var conversation = await harness.Store.GetConversationAsync(conversationId, CancellationToken.None);
        Assert.NotNull(conversation);
        var messages = await harness.Store.ReadMessagesAsync(conversationId, CancellationToken.None);
        Assert.Equal(
            FixtureSourceAdapter.BuildMessages(FixtureSourceAdapter.GroupConversation).Count,
            messages.Count);
    }

    [Fact]
    public async Task SyncJsonEmitsOneDocumentWithCountersAndDiagnostics()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = ParseSingleJson(stdout);
        var root = doc.RootElement;
        Assert.Equal(GroupStableId(harness.Adapter), root.GetProperty("conversation_id").GetString());
        Assert.StartsWith("a_", root.GetProperty("account_id").GetString());
        Assert.True(root.GetProperty("counters").GetProperty("inserted").GetInt32() > 0);
        Assert.True(root.GetProperty("records_scanned").GetInt32() > 0);

        var diagnostics = root.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.Contains(diagnostics, d => d.GetProperty("code").GetString() == "unknown_message_type");
        Assert.All(diagnostics, d => Assert.True(d.GetProperty("count").GetInt32() >= 1));

        // Progress stays on stderr; stdout carries only the result document.
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task SyncHumanWritesProgressToStderrAndResultToStdout()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation],
            stdout, stderr);

        Assert.Equal(ExitCode.Success, exit);
        var stdoutContent = stdout.ToString();
        var stderrContent = stderr.ToString();

        Assert.Contains("Synced conversation", stdoutContent);
        Assert.Contains("Resolving", stderrContent);
        Assert.DoesNotContain("Resolving", stdoutContent);
        Assert.DoesNotContain("{", stdoutContent);
    }

    [Fact]
    public async Task SyncUsesTheSameStableIdMechanismAsTheImporter()
    {
        // The CLI must not introduce a second identity/alias store: the conversation id it
        // reports is exactly the stable id ImportService derives from the source profile and
        // the upstream conversation id.
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();

        await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            stdout, TextWriter.Null);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(GroupStableId(harness.Adapter), doc.RootElement.GetProperty("conversation_id").GetString());
    }

    [Fact]
    public async Task SyncIsIdempotentAcrossRuns()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);

        await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            new StringWriter(), TextWriter.Null);
        var afterFirst = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        var stdout = new StringWriter();
        await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            stdout, TextWriter.Null);
        var afterSecond = await harness.Store.GetArchiveStatsAsync(CancellationToken.None);

        Assert.Equal(afterFirst.MessageCount, afterSecond.MessageCount);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(afterFirst.MessageCount, doc.RootElement.GetProperty("counters").GetProperty("unchanged").GetInt32());
    }

    // ---- sync: R2 fatal source-coverage failure -> full rollback, exit 1 ----

    [Fact]
    public async Task SyncFatalSourceCoverageRollsBackTheConversationAndExitsOne()
    {
        using var temp = new TempDirectory();
        var adapter = new UnreadableShardSource();
        using var harness = CreateHarness(temp, adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", UnreadableShardSource.ConversationId, "--json"],
            stdout, stderr);

        Assert.Equal(ExitCode.Failure, exit);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Failure, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("unreadable", doc.RootElement.GetProperty("error").GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        // R2: a Fatal source-coverage failure rolls back the entire conversation transaction.
        // The archive holds no conversation row, no messages, and no aggregates counting data
        // it does not have. This is re-verified against a freshly reopened store.
        var conversationId = StableIds.GroupConversation(
            StableIds.Account(adapter.AdapterName, UnreadableShardSource.ProfileId),
            UnreadableShardSource.ConversationId);

        await AssertArchiveHasNoConversationAsync(harness.Store, conversationId);
        var reopened = new SqliteArchiveStore(harness.ArchivePath, harness.Clock);
        await AssertArchiveHasNoConversationAsync(reopened, conversationId);

        Assert.Contains("error:", stderr.ToString());
    }

    private static async Task AssertArchiveHasNoConversationAsync(IArchiveStore store, string conversationId)
    {
        Assert.Null(await store.GetConversationAsync(conversationId, CancellationToken.None));
        Assert.Empty(await store.ReadMessagesAsync(conversationId, CancellationToken.None));
        var stats = await store.GetArchiveStatsAsync(CancellationToken.None);
        Assert.Equal(0, stats.ConversationCount);
        Assert.Equal(0, stats.MessageCount);
    }

    // ---- sync: exit codes, --no-input, cancellation ----

    [Fact]
    public async Task SyncMissingConversationFlagExitsTwo()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
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
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider, ["sync", "--bogus", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.UsageError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncNoInputNeverPromptsAndSucceeds()
    {
        // The command auto-selects the current source account, so --no-input has no prompt
        // path to take and a normal sync still succeeds.
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--no-input", "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(GroupStableId(harness.Adapter), doc.RootElement.GetProperty("conversation_id").GetString());
    }

    [Fact]
    public async Task SyncConversationNotFoundExitsOne()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", "does-not-exist", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Failure, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncCancellationExits130()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exit = await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            stdout, stderr, cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Cancelled, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- export: happy path, documented package, JSON ----

    [Fact]
    public async Task ExportSuccessPublishesTheDocumentedPackageAndExitsZero()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
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
        using var harness = CreateHarness(temp);
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
        using var harness = CreateHarness(temp);
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
        // Both commands resolve the selector through the source catalog by the upstream
        // conversation id, and the workflow derives the stable archive id exactly as the
        // importer does — no second alias/id store is introduced by the CLI.
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        await RunAsync(harness.Provider,
            ["export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json"],
            stdout, TextWriter.Null);

        using var doc = ParseSingleJson(stdout);
        Assert.Equal(GroupStableId(harness.Adapter), doc.RootElement.GetProperty("conversation_ids")[0].GetString());
    }

    // ---- export: R1 caught failure restores prior output, exit 1 ----

    [Fact]
    public async Task ExportCaughtFailureRestoresPriorOutputAndExitsOne()
    {
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp, exporterFactory: store => new FaultingExporter(store));
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
        using var harness = CreateHarness(temp);
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
        using var harness = CreateHarness(temp);
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider, ["export", "--json"], stdout, TextWriter.Null);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.UsageError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExportSourceCoverageFailureProducesNoPackageAndExitsOne()
    {
        using var temp = new TempDirectory();
        var adapter = new UnreadableShardSource();
        using var harness = CreateHarness(temp, adapter);
        var output = temp.Combine("out");
        var stdout = new StringWriter();

        var exit = await RunAsync(harness.Provider,
            ["export", "--conversation", UnreadableShardSource.ConversationId, "--output", output, "--json"],
            stdout, TextWriter.Null);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseSingleJson(stdout);
        Assert.Equal(CliErrorCode.Failure, doc.RootElement.GetProperty("error").GetProperty("code").GetString());

        // No dataset package may be produced for a conversation whose source is unavailable:
        // the exporter is never reached because the import phase rolled back (R2).
        Assert.False(Directory.Exists(output));
    }

    // ---- no R3+ recovery machinery ----

    [Fact]
    public async Task NoPersistentRecoveryStateIsCreatedBySyncOrExport()
    {
        // The hard-stop rule forbids journals, commit markers, recovery ledgers or cross-file
        // transaction protocols beyond SQLite's own documented boundary. This test asserts
        // that after a successful sync+export, only SQLite's own files and the documented
        // export package remain — no new persistent recovery state.
        using var temp = new TempDirectory();
        using var harness = CreateHarness(temp);
        var output = temp.Combine("out");

        await RunAsync(harness.Provider,
            ["sync", "--conversation", FixtureSourceAdapter.GroupConversation, "--json"],
            new StringWriter(), TextWriter.Null);
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
        using var harness = CreateHarness(temp);
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

    // ---- stubs ----

    /// <summary>
    /// A source whose message shard is unavailable: <see cref="ReadMessagesAsync"/> throws
    /// <see cref="SourceCoverageException"/> instead of yielding an empty stream, so a sync
    /// surfaces a Fatal diagnostic and a rolled-back transaction (FR-14).
    /// </summary>
    private sealed class UnreadableShardSource : ISourceAdapter
    {
        public const string ProfileId = "stub_account";
        public const string ConversationId = "stub_conv";

        public string AdapterName => "stub";
        public string AdapterVersion => "1.0.0";

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                SourceVersion = "stub-1",
                SourceProductName = "stub source",
                IsAvailable = true,
            });

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceAccount>>(
            [
                new SourceAccount { SourceProfileId = ProfileId, DisplayName = "stub account", IsCurrent = true },
            ]);

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceConversation>>(
            [
                new SourceConversation { SourceConversationId = ConversationId, Kind = ConversationKind.Group, Title = "stub" },
            ]);

        public Task<SourceConversationDetail> DescribeConversationAsync(string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            Task.FromResult(new SourceConversationDetail { SourceConversationId = sourceConversationId, MessageCount = 5, ParticipantCount = 1 });

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceParticipant>>([new SourceParticipant { SourceUserId = ProfileId, Nickname = "stub" }]);

        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken)
        {
            throw new SourceCoverageException(DiagnosticCodes.PartitionUnreadable, "stub shard unreadable");
        }
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
}
