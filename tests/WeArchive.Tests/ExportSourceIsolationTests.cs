using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Issue #66 acceptance coverage: <c>wearchive export --conversation ...</c> is a read-only
/// derivation from the current canonical archive
/// (<c>Canonical SQLite -&gt; JSONL/YAML/JSON</c>).
/// <para>
/// Source isolation is proven with a source adapter that throws on every interface method while
/// export still succeeds, and canonical read-only behavior is proven by fingerprinting every
/// canonical/audit/checkpoint table (and the Raw Vault) before and after an export. The
/// sync-versus-export semantics run the shipped composition root over the synthetic WeChat source,
/// so <c>sync</c> stays the only refresh path and export is measured purely as a derivation
/// (docs/PRD.md G7/G10/FR-12, ADR 0008, docs/EXPORT_PRD.md sections 3.2 and 15).
/// </para>
/// </summary>
public sealed class ExportSourceIsolationTests
{
    private static readonly FixtureSourceAdapter Fixture = new();

    private static string FixtureAccountStableId =>
        StableIds.Account(Fixture.AdapterName, FixtureSourceAdapter.FixtureAccountId);

    private static string GroupStableId =>
        StableIds.Conversation(
            FixtureAccountStableId, ConversationKind.Group, FixtureSourceAdapter.GroupConversation, null);

    private static string DirectStableId =>
        StableIds.Conversation(
            FixtureAccountStableId, ConversationKind.Direct, FixtureSourceAdapter.DirectConversation, FixtureSourceAdapter.Alice);

    // ---- fixture-backed harness (canonical archive already published) -------

    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }

        public required string ArchivePath { get; init; }

        public required string RawVaultRoot { get; init; }

        public required RecordingSource Source { get; init; }

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>
    /// Publishes the fixture conversations through the canonical publication engine (the engine an
    /// explicit <c>sync</c> drives) and then builds the shipped composition root over that archive
    /// with the supplied source adapter — so a test can inject a source that must never be reached.
    /// </summary>
    private static async Task<Harness> CreateHarnessAsync(TempDirectory temp, RecordingSource source)
    {
        var clock = new FixedClock();
        var archivePath = temp.Combine("archive.db");
        var rawVaultRoot = temp.Combine("rawvault");

        var seedStore = new SqliteArchiveStore(archivePath, clock);
        var seeder = new ImportService(new FixtureSourceAdapter(), seedStore, clock);
        await seeder.ImportConversationAsync(new ImportRequest
        {
            SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
            SourceConversationId = FixtureSourceAdapter.GroupConversation,
            Kind = ConversationKind.Group,
            ConversationTitle = "华东产品创新中心工作群",
        }, null, CancellationToken.None).ConfigureAwait(false);
        await seeder.ImportConversationAsync(new ImportRequest
        {
            SourceProfileId = FixtureSourceAdapter.FixtureAccountId,
            SourceConversationId = FixtureSourceAdapter.DirectConversation,
            Kind = ConversationKind.Direct,
            PeerSourceUserId = FixtureSourceAdapter.Alice,
            ConversationTitle = "张三",
        }, null, CancellationToken.None).ConfigureAwait(false);

        var services = new ServiceCollection();
        services.AddWeArchiveCore(archivePath, rawVaultRoot);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISourceAdapter>(source);
        services.AddSingleton<ISourceCaptureAdapter, FixtureCaptureAdapter>();
        services.AddSingleton(new CliExportDefaults { DefaultOutputDirectory = temp.Combine("export") });

        return new Harness
        {
            Provider = services.BuildServiceProvider(),
            ArchivePath = archivePath,
            RawVaultRoot = rawVaultRoot,
            Source = source,
        };
    }

    // ---- offline / source isolation -----------------------------------------

    [Fact]
    public async Task ExportSucceedsWhenLiveSourceIsUnavailable()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);
        var output = temp.Combine("out");

        var run = await RunAsync(harness.Provider,
            "export", "--conversation", GroupStableId, "--output", output, "--json");

        Assert.Equal(ExitCode.Success, run.ExitCode);
        Assert.Equal(0, source.CallCount);

        // The documented package layout is published without any live WeChat access.
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(output, "identities.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "conversations.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "collections.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "chats", "groups", GroupStableId, "2026", "2026-01.jsonl")));

        using var document = ParseSingleJson(run.Stdout);
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(GroupStableId, document.RootElement.GetProperty("conversation_ids")[0].GetString());
        Assert.True(document.RootElement.GetProperty("record_count").GetInt32() > 0);
    }

    [Fact]
    public async Task ExportNeverCallsSourceAdapter()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);

        // Successful selectors (stable id, unique archived upstream id, direct conversation) and a
        // failing selector alike must leave the source-adapter call count at exactly zero.
        var stable = await RunAsync(harness.Provider,
            "export", "--conversation", GroupStableId, "--output", temp.Combine("stable"), "--json");
        var upstream = await RunAsync(harness.Provider,
            "export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", temp.Combine("upstream"), "--json");
        var direct = await RunAsync(harness.Provider,
            "export", "--conversation", DirectStableId, "--output", temp.Combine("direct"), "--json");
        var unknown = await RunAsync(harness.Provider,
            "export", "--conversation", "g_0000000000000000", "--output", temp.Combine("unknown"), "--json");

        Assert.Equal(ExitCode.Success, stable.ExitCode);
        Assert.Equal(ExitCode.Success, upstream.ExitCode);
        Assert.Equal(ExitCode.Success, direct.ExitCode);
        Assert.Equal(ExitCode.Failure, unknown.ExitCode);
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task ExportResolvesStableIdFromArchive()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);
        var output = temp.Combine("out");

        var run = await RunAsync(harness.Provider,
            "export", "--conversation", GroupStableId, "--output", output, "--json");

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(GroupStableId, document.RootElement.GetProperty("conversation_ids")[0].GetString());
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task ExportResolvesUniqueArchivedSourceIdOffline()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);
        var output = temp.Combine("out");

        // The upstream id is accepted because it resolves uniquely among archived conversations.
        var run = await RunAsync(harness.Provider,
            "export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json");

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(GroupStableId, document.RootElement.GetProperty("conversation_ids")[0].GetString());
        Assert.True(File.Exists(Path.Combine(output, "manifest.json")));
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task ExportRejectsAmbiguousArchivedSourceId()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);

        // A second archived account exposes the same upstream source conversation id.
        var store = new SqliteArchiveStore(harness.ArchivePath, new FixedClock());
        await store.UpsertAccountAsync(new ArchiveAccount
        {
            Id = "a_other_account",
            SourceProfileId = "other_profile",
            AdapterName = "fixture",
        }, CancellationToken.None);
        await store.UpsertConversationsAsync(
        [
            new ArchiveConversation
            {
                Id = "g_conflicting_upstream_id",
                AccountId = "a_other_account",
                SourceConversationId = FixtureSourceAdapter.GroupConversation,
                Kind = ConversationKind.Group,
                Title = "duplicate upstream id",
            },
        ], CancellationToken.None);

        var output = temp.Combine("out");
        var run = await RunAsync(harness.Provider,
            "export", "--conversation", FixtureSourceAdapter.GroupConversation, "--output", output, "--json");

        Assert.Equal(ExitCode.Failure, run.ExitCode);
        Assert.Equal(0, source.CallCount);

        using var document = ParseSingleJson(run.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(CliErrorCode.ConversationAmbiguous, error.GetProperty("code").GetString());

        // Deterministic: both candidates are named in stable-id order, and no account is chosen.
        var message = error.GetProperty("message").GetString()!;
        var ordered = new[] { GroupStableId, "g_conflicting_upstream_id" }
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Contains(ordered[0], message, StringComparison.Ordinal);
        Assert.Contains(ordered[1], message, StringComparison.Ordinal);
        Assert.True(
            message.IndexOf(ordered[0], StringComparison.Ordinal) < message.IndexOf(ordered[1], StringComparison.Ordinal),
            "the ambiguity report must list candidates deterministically");

        Assert.False(Directory.Exists(output), "an ambiguous selector must publish no package");

        // The canonical stable id stays the unambiguous selector for the same archive.
        var stable = await RunAsync(harness.Provider,
            "export", "--conversation", GroupStableId, "--output", output, "--json");
        Assert.Equal(ExitCode.Success, stable.ExitCode);
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task ExportUnknownConversationFailsWithoutSourceAccess()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);
        var output = temp.Combine("out");

        var run = await RunAsync(harness.Provider,
            "export", "--conversation", "does-not-exist", "--output", output, "--json");

        Assert.Equal(ExitCode.Failure, run.ExitCode);
        Assert.Equal(0, source.CallCount);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(CliErrorCode.ConversationNotFound, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(output), "an unknown conversation must publish no package");
    }

    // ---- canonical read-only behavior ---------------------------------------

    [Fact]
    public async Task ExportDoesNotMutateCanonicalArchiveOrCheckpoints()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);

        // Make canonical/checkpoint state non-trivial so the comparison below is meaningful.
        var store = new SqliteArchiveStore(harness.ArchivePath, new FixedClock());
        await store.SetIngestCheckpointAsync(new IngestCheckpoint
        {
            Id = "cp_export_isolation",
            AccountId = FixtureAccountStableId,
            AdapterFamily = "fixture",
            ScopeKind = "conversation",
            ScopeId = GroupStableId,
            CheckpointJson = "{\"cursor\":1}",
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        var before = await FingerprintAsync(harness.ArchivePath);
        Assert.NotEmpty(before);

        var run = await RunAsync(harness.Provider,
            "export", "--conversation", GroupStableId, "--output", temp.Combine("out"), "--json");
        Assert.Equal(ExitCode.Success, run.ExitCode);

        // Canonical tables, import-run audit rows and both checkpoint tables are byte-identical.
        var after = await FingerprintAsync(harness.ArchivePath);
        Assert.Equal(before, after);

        // Export never reads or writes the Raw Vault; it publishes no generation.
        var vault = harness.Provider.GetRequiredService<IRawVaultStore>();
        Assert.Empty(await vault.ListGenerationsAsync(FixtureAccountStableId, CancellationToken.None));
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task ExportProjectsPersistedDiagnosticsWithoutLiveSourceAccess()
    {
        using var temp = new TempDirectory();
        var source = new RecordingSource(new FixtureSourceAdapter(), throwing: true);
        using var harness = await CreateHarnessAsync(temp, source);
        var output = temp.Combine("out");

        var run = await RunAsync(harness.Provider,
            "export", "--conversation", GroupStableId, "--output", output, "--json");

        Assert.Equal(ExitCode.Success, run.ExitCode);
        Assert.Equal(0, source.CallCount);

        // Manifest diagnostics are projected from the persisted import-run audit state.
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        var manifestDiagnostics = manifest.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
        Assert.NotEmpty(manifestDiagnostics);
        Assert.Contains(manifestDiagnostics, d => d.GetProperty("code").GetString() == DiagnosticCodes.UnknownMessageType);
        Assert.All(manifestDiagnostics, d => Assert.True(d.GetProperty("count").GetInt32() >= 1));

        using var document = ParseSingleJson(run.Stdout);
        Assert.Contains(
            document.RootElement.GetProperty("diagnostics").EnumerateArray(),
            d => d.GetProperty("code").GetString() == DiagnosticCodes.UnknownMessageType);
    }

    // ---- sync owns refresh; export owns derivation ---------------------------

    [Fact]
    public async Task ExportWithoutSyncDoesNotPullNewLiveMessages()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.GroupId("66001"),
            Text = "A",
            MessageCount = 2,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);
        var stableId = harness.StableId(conversation.SourceConversationId);

        // An explicit sync publishes canonical state A.
        var synced = await RunAsync(harness.Provider,
            "sync", "--conversation", conversation.SourceConversationId, "--json");
        Assert.Equal(ExitCode.Success, synced.ExitCode);
        var archived = await harness.MessagesAsync(stableId);
        Assert.Equal(2, archived.Count);

        // Live WeChat now contains a newer message B that was never explicitly synced.
        conversation.MessageCount = 3;
        harness.AdvanceClock();
        harness.Source.IsAvailable = false; // any live read would now fail

        var output = temp.Combine("out-a");
        var run = await RunAsync(harness.Provider,
            "export", "--conversation", stableId, "--output", output, "--json");

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(archived.Count, document.RootElement.GetProperty("record_count").GetInt32());

        var timeline = await ReadTimelineAsync(output);
        Assert.Contains("A 2", timeline, StringComparison.Ordinal);
        Assert.DoesNotContain("A 3", timeline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAfterExplicitSyncContainsNewCanonicalMessages()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.GroupId("66002"),
            Text = "A",
            MessageCount = 2,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);
        var stableId = harness.StableId(conversation.SourceConversationId);

        var firstSync = await RunAsync(harness.Provider,
            "sync", "--conversation", conversation.SourceConversationId, "--json");
        Assert.Equal(ExitCode.Success, firstSync.ExitCode);
        Assert.Equal(2, (await harness.MessagesAsync(stableId)).Count);

        // An explicit sync updates the canonical state to A+B.
        conversation.MessageCount = 3;
        harness.AdvanceClock();
        var secondSync = await RunAsync(harness.Provider,
            "sync", "--conversation", conversation.SourceConversationId, "--json");
        Assert.Equal(ExitCode.Success, secondSync.ExitCode);
        Assert.Equal(3, (await harness.MessagesAsync(stableId)).Count);

        var output = temp.Combine("out-ab");
        var run = await RunAsync(harness.Provider,
            "export", "--conversation", stableId, "--output", output, "--json");

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(3, document.RootElement.GetProperty("record_count").GetInt32());

        var timeline = await ReadTimelineAsync(output);
        Assert.Contains("A 3", timeline, StringComparison.Ordinal);
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<CliRun> RunAsync(ServiceProvider provider, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CliHost.RunAsync(args, stdout, stderr, provider).ConfigureAwait(false);
        return new CliRun(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument ParseSingleJson(string output)
    {
        var trimmed = output.TrimEnd();
        Assert.NotEmpty(trimmed);
        Assert.False(trimmed.Contains('\n'), $"stdout must contain exactly one JSON document, got: {trimmed}");
        Assert.False(trimmed.Contains('\u001b'), "stdout must not carry ANSI escape decoration");
        return JsonDocument.Parse(trimmed);
    }

    private static async Task<string> ReadTimelineAsync(string root)
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            builder.AppendLine(await File.ReadAllTextAsync(file).ConfigureAwait(false));
        }

        return builder.ToString();
    }

    /// <summary>
    /// A stable digest of every canonical, audit and checkpoint table (every column, rows sorted by
    /// content so physical row order cannot matter), so a test can prove an operation changed none
    /// of them.
    /// </summary>
    private static async Task<string> FingerprintAsync(string archivePath)
    {
        var builder = new StringBuilder();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = archivePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);

        string[] tables =
        [
            "accounts",
            "participants",
            "conversations",
            "messages",
            "import_runs",
            "ingest_checkpoints",
            "source_checkpoints",
        ];

        foreach (var table in tables)
        {
            var rows = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT * FROM {table};";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var row = new StringBuilder();
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row.Append(reader.IsDBNull(i) ? "<null>" : FormatValue(reader.GetValue(i)));
                        row.Append('|');
                    }

                    rows.Add(row.ToString());
                }
            }

            rows.Sort(StringComparer.Ordinal);
            builder.Append("table:").Append(table).Append('\n');
            foreach (var row in rows)
            {
                builder.Append(row).Append('\n');
            }

            builder.Append("==\n");
        }

        return builder.ToString();
    }

    private static string FormatValue(object value) => value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// A source adapter that counts every interface call and (when configured) throws on every
    /// method. The zero-call count after a successful export is the Issue #66 proof that export
    /// performs no source-adapter call at all.
    /// </summary>
    private sealed class RecordingSource(ISourceAdapter inner, bool throwing) : ISourceAdapter
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public string AdapterName => inner.AdapterName;

        public string AdapterVersion => inner.AdapterVersion;

        private void Record()
        {
            Interlocked.Increment(ref _callCount);
            if (throwing)
            {
                throw new InvalidOperationException("the live WeChat source is unavailable");
            }
        }

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken)
        {
            Record();
            return inner.DescribeSourceAsync(cancellationToken);
        }

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            Record();
            return inner.ListAccountsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId,
            CancellationToken cancellationToken)
        {
            Record();
            return inner.ListConversationsAsync(sourceProfileId, cancellationToken);
        }

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId,
            string sourceConversationId,
            CancellationToken cancellationToken)
        {
            Record();
            return inner.DescribeConversationAsync(sourceProfileId, sourceConversationId, cancellationToken);
        }

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId,
            CancellationToken cancellationToken)
        {
            Record();
            return inner.ListParticipantsAsync(sourceProfileId, cancellationToken);
        }

        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId,
            string sourceConversationId,
            CancellationToken cancellationToken)
        {
            Record();
            return inner.ReadMessagesAsync(sourceProfileId, sourceConversationId, cancellationToken);
        }
    }
}