using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.Compatibility;
using WeArchive.Tests.Support;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WeArchive.Tests;

/// <summary>
/// Issue #100 (Roadmap M2d, PRD G5/G9 FR-19/22): contact/remark metadata refresh must never
/// move a stable identity or a physical export path, must merge only evidence-supported
/// updates and must stay deterministic across incremental canonical ingest and the offline
/// Raw Vault rebuild.
/// <para>
/// Every scenario runs through the shipped captured-generation pipeline only: a synthetic
/// WeChat 4.x Raw Vault generation (v2 store, manifest 3) is ingested by
/// <see cref="RawVaultIngestService"/> and exported by <see cref="JsonlDatasetExporter"/> /
/// rebuilt by <see cref="RebuildService"/>. No live WeChat client, no key acquirer and no
/// network is involved, which is itself the acceptance rule "no new dependency on live
/// WeChat for canonical-only export". The group conversation spans two rotation windows
/// (message_1.db oldest, message_0.db newest) so every scenario exercises rotated shards
/// (Issue #71). No canonical schema, message schema, architecture or reliability change;
/// these tests pin documented behavior (EXPORT_PRD sections 3.1/5, DATA_MODEL section 6).
/// </para>
/// </summary>
public sealed class MetadataRefreshDeterminismTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(8));

    // The export manifest's created_at is the only non-reproducible package field; pinning it
    // (ExportRequest.CreatedAt) makes whole-package byte determinism assertable.
    private static readonly DateTimeOffset PinnedExportAt = new(2026, 3, 2, 0, 0, 0, TimeSpan.FromHours(8));

    private const string Profile = "wxid_alice";
    private const string DirectConversationId = "wxid_bob";
    private const string GroupConversationId = "100200300@chatroom";

    // Group senders: Carol and Dave exist in contact.db (Dave without any metadata); Ghost is
    // only in the shards' Name2Id tables, so the adapter must leave its sender unresolved
    // instead of inventing an identity.
    private const string Carol = "wxid_carol";
    private const string Dave = "wxid_dave";
    private const string Ghost = "wxid_ghost";

    /// <summary>One contact.db row; a null entry removes the row between generations.</summary>
    private sealed record TestContact(string? Remark, string? Nickname);

    private static string AccountId => StableIds.Account(WeChatWindowsSourceAdapter.Name, Profile);
    private static string BobParticipantId => StableIds.Participant(AccountId, DirectConversationId);
    private static string CarolParticipantId => StableIds.Participant(AccountId, Carol);
    private static string DaveParticipantId => StableIds.Participant(AccountId, Dave);
    private static string DirectConversationStableId => StableIds.Conversation(
        AccountId, ConversationKind.Direct, DirectConversationId, DirectConversationId);
    private static string GroupConversationStableId => StableIds.Conversation(
        AccountId, ConversationKind.Group, GroupConversationId, null);

    [Fact]
    public async Task MetadataRefreshKeepsStableIdsPathsAndProvenanceAcrossIncrementalIngest()
    {
        // Acceptance: a remark rename refreshes display metadata only — participant/conversation
        // stable ids, physical export paths, message bytes and per-message provenance stay
        // byte-identical, and the re-export is deterministic.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        var generation1 = await PublishGenerationAsync(vault, temp.Path, CapturedAt, Contacts(
            (DirectConversationId, new TestContact("旧备注 Old", "Bob")),
            (Carol, new TestContact("同名人 Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        var archivePath = temp.Combine("archive", "wearchive.db");
        var archive = new SqliteArchiveStore(archivePath, new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);

        var output1 = temp.Combine("export-1");
        await ExportEverythingAsync(archive, output1);
        var before = Snapshot(output1);

        // Contact-only generation: identical message shards, renamed remark.
        var generation2 = await PublishGenerationAsync(vault, temp.Path, CapturedAt.AddHours(1), Contacts(
            (DirectConversationId, new TestContact("新备注 New", "Bob")),
            (Carol, new TestContact("同名人 Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        Assert.NotEqual(generation1.GenerationId, generation2.GenerationId);
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);

        var participant = Assert.Single(
            await archive.ListParticipantsAsync(AccountId, CancellationToken.None),
            p => p.Id == BobParticipantId);
        Assert.Equal("新备注 New", participant.LatestRemark);
        Assert.Equal(DirectConversationId, participant.SourceParticipantId);

        var output2 = temp.Combine("export-2");
        await ExportEverythingAsync(archive, output2);
        var after = Snapshot(output2);

        // Stable paths: the same relative file set, and every exported message/provenance file
        // is byte-identical. Only display catalogs may change.
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (relative, sha) in before.Where(kv => kv.Key.StartsWith("chats/", StringComparison.Ordinal)))
        {
            Assert.Equal(sha, after[relative]);
        }

        var identities = ReadIdentities(output2);
        var bobEntry = Assert.Contains(BobParticipantId, identities);
        Assert.Equal(DirectConversationId, bobEntry["source_user_id"]);
        Assert.Equal("新备注 New", bobEntry["remark"]);
        Assert.Equal("新备注 New", bobEntry["display_name"]);
        Assert.Equal(string.Empty, bobEntry["display_name_override"]);

        // Deterministic re-export: same archive state, pinned created_at, byte-identical package.
        var output3 = temp.Combine("export-3");
        await ExportEverythingAsync(archive, output3);
        Assert.Equal(after, Snapshot(output3));
    }

    [Fact]
    public async Task DuplicateDisplayNamesStayDistinctStableIdentities()
    {
        // Acceptance: two participants that share remark and nickname never merge — display
        // names can collide, stable ids cannot (DATA_MODEL section 6).
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        await PublishGenerationAsync(vault, temp.Path, CapturedAt, Contacts(
            (DirectConversationId, new TestContact("同一个人", "Twin")),
            (Carol, new TestContact("同一个人", "Twin")),
            (Dave, new TestContact(null, null))));
        var archive = new SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);

        var output = temp.Combine("export");
        await ExportEverythingAsync(archive, output);

        var identities = ReadIdentities(output);
        var bobEntry = Assert.Contains(BobParticipantId, identities);
        var carolEntry = Assert.Contains(CarolParticipantId, identities);
        Assert.NotEqual(BobParticipantId, CarolParticipantId);
        Assert.Equal("同一个人", bobEntry["remark"]);
        Assert.Equal("同一个人", carolEntry["remark"]);
        Assert.Equal("Twin", bobEntry["nickname"]);
        Assert.Equal("Twin", carolEntry["nickname"]);
        Assert.Equal(DirectConversationId, bobEntry["source_user_id"]);
        Assert.Equal(Carol, carolEntry["source_user_id"]);

        // Each sender keeps its own stable id in the timelines.
        var directLine = ReadJsonLines(Path.Combine(output, "chats", "direct", DirectConversationStableId))
            .Single(line => (string)line["text"]! == "direct hello");
        Assert.Equal(BobParticipantId, (string)directLine["sender_id"]!);
        var groupLines = ReadJsonLines(Path.Combine(output, "chats", "groups", GroupConversationStableId));
        Assert.Contains(groupLines, line => (string)line["sender_id"]! == CarolParticipantId);
    }

    [Fact]
    public async Task MissingParticipantMetadataExportsStableIdOnlyAndUnresolvableSendersStayNull()
    {
        // Acceptance (baseline F1/F2): a referenced sender without contact metadata is published
        // by stable id with empty display data, and a sender the evidence cannot resolve stays
        // null — never dropped, never invented.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        await PublishGenerationAsync(vault, temp.Path, CapturedAt, Contacts(
            (DirectConversationId, new TestContact("Bob's remark", "Bob")),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        var archive = new SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);

        var output = temp.Combine("export");
        await ExportEverythingAsync(archive, output);

        var groupLines = ReadJsonLines(Path.Combine(output, "chats", "groups", GroupConversationStableId));
        var daveLine = Assert.Single(groupLines, line => (string)line["text"]! == "group oldest from dave");
        Assert.Equal(DaveParticipantId, (string)daveLine["sender_id"]!);
        var ghostLine = Assert.Single(groupLines, line => (string)line["text"]! == "group newest from ghost");
        Assert.Null(ghostLine["sender_id"]);

        var identities = ReadIdentities(output);
        var daveEntry = Assert.Contains(DaveParticipantId, identities);
        Assert.Equal(Dave, daveEntry["source_user_id"]);
        Assert.Equal(string.Empty, daveEntry["remark"]);
        Assert.Equal(string.Empty, daveEntry["nickname"]);
        Assert.Equal(string.Empty, daveEntry["display_name"]);
        Assert.DoesNotContain(identities.Keys, key => identities[key]["source_user_id"] == Ghost);

        // The archive holds no fabricated participant row for the unresolvable sender either.
        Assert.DoesNotContain(
            await archive.ListParticipantsAsync(AccountId, CancellationToken.None),
            p => p.SourceParticipantId == Ghost);
    }

    [Fact]
    public async Task WithdrawnOrClearedContactMetadataIsNotSilentlyErased()
    {
        // Acceptance: a merge happens only when evidence supports it. A newer capture with a
        // cleared remark (or no contact row at all) provides no replacement value, so the last
        // observed evidence stays archived and exported; ids never move.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        await PublishGenerationAsync(vault, temp.Path, CapturedAt, Contacts(
            (DirectConversationId, new TestContact("Old remark", "Bob")),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        var archive = new SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);
        Assert.Equal("Old remark", await RemarkOfAsync(archive));

        // Cleared remark: the row exists but carries no value (Normalize -> null), so the
        // null-safe merge keeps the last non-null evidence.
        await PublishGenerationAsync(vault, temp.Path, CapturedAt.AddHours(1), Contacts(
            (DirectConversationId, new TestContact("", "Bob")),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);
        Assert.Equal("Old remark", await RemarkOfAsync(archive));

        // Withdrawn row: the contact disappears entirely; absence never erases archived facts.
        await PublishGenerationAsync(vault, temp.Path, CapturedAt.AddHours(2), Contacts(
            (DirectConversationId, null),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);
        Assert.Equal("Old remark", await RemarkOfAsync(archive));

        var output = temp.Combine("export");
        await ExportEverythingAsync(archive, output);
        var identities = ReadIdentities(output);
        Assert.Equal("Old remark", Assert.Contains(BobParticipantId, identities)["remark"]);
    }

    private static async Task<string?> RemarkOfAsync(SqliteArchiveStore archive) => (
        await archive.ListParticipantsAsync(AccountId, CancellationToken.None).ConfigureAwait(false)
    ).Single(p => p.Id == BobParticipantId).LatestRemark;

    [Fact]
    public async Task UserDisplayNameOverrideSurvivesReexportAndUnreferencedCarryover()
    {
        // Acceptance: the user-maintained display_name_override is a higher-trust fact
        // (EXPORT_PRD sections 5.3/5.4): it wins over regenerated archive values and is never
        // silently discarded — not for referenced identities and not for unreferenced ones.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        await PublishGenerationAsync(vault, temp.Path, CapturedAt, Contacts(
            (DirectConversationId, new TestContact("Archived remark", "Bob")),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))));
        var archive = new SqliteArchiveStore(temp.Combine("archive", "wearchive.db"), new FixedClock());
        var ingester = new RawVaultIngestService(vault, archive, new FixedClock());
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);

        var output = temp.Combine("export");
        await ExportEverythingAsync(archive, output);
        EditIdentities(output, users =>
        {
            users[BobParticipantId]["display_name_override"] = "My Bob";
            users["u_manual_override_only"] = new Dictionary<string, string>
            {
                ["source_user_id"] = "wxid_manual",
                ["remark"] = "",
                ["nickname"] = "",
                ["display_name"] = "",
                ["display_name_override"] = "Kept",
            };
        });

        // Re-export over the same root: the override wins, archive values regenerate, and the
        // unreferenced manual correction is carried over verbatim.
        await ExportEverythingAsync(archive, output);

        var identities = ReadIdentities(output);
        var bobEntry = Assert.Contains(BobParticipantId, identities);
        // Documented merge policy (EXPORT_PRD 5.3/5.4): display_name is always regenerated from
        // the archive; the user-maintained override is carried over verbatim and wins at
        // resolution time (IdentityEntry.Effective).
        Assert.Equal("My Bob", bobEntry["display_name_override"]);
        Assert.Equal("Archived remark", bobEntry["display_name"]);
        Assert.Equal("Archived remark", bobEntry["remark"]);
        Assert.Equal("Kept", identities["u_manual_override_only"]["display_name_override"]);
    }

    [Fact]
    public async Task OfflineRebuildWithRotatedShardsReproducesCanonicalIdentityAndRefusesPartialCoverage()
    {
        // Acceptance: the offline Raw Vault rebuild produces the same canonical identities,
        // provenance and export package as incremental ingest (the group spans message_1.db +
        // message_0.db rotation windows), and a partial generation is refused without touching
        // the existing archive.
        using var temp = new TempDirectory();
        var vault = new RawVaultStore(temp.Combine("vault"));
        await PublishGenerationAsync(vault, temp.Path, CapturedAt, Contacts(
            (DirectConversationId, new TestContact("Old remark", "Bob")),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))));

        var archiveAPath = temp.Combine("archive-a", "wearchive.db");
        var archiveA = new SqliteArchiveStore(archiveAPath, new FixedClock());
        var ingester = new RawVaultIngestService(vault, archiveA, new FixedClock());
        await ingester.IngestAsync(AccountId, null, null, CancellationToken.None);

        var archiveBPath = temp.Combine("archive-b", "wearchive.db");
        await new RebuildService(vault, archiveBPath, new FixedClock()).RebuildAsync(null, CancellationToken.None);
        var archiveB = new SqliteArchiveStore(archiveBPath, new FixedClock());

        await AssertArchivesEqualAsync(archiveA, archiveB);

        var outputA = temp.Combine("export-a");
        var outputB = temp.Combine("export-b");
        await ExportEverythingAsync(archiveA, outputA);
        await ExportEverythingAsync(archiveB, outputB);
        Assert.Equal(Snapshot(outputA), Snapshot(outputB));

        // A later partial generation cannot establish a canonical rebuild: the refusal leaves
        // the existing archive untouched (R2 fail-closed, docs/DATA_MODEL.md section 21.8).
        await PublishGenerationAsync(vault, temp.Path, CapturedAt.AddHours(3), Contacts(
            (DirectConversationId, new TestContact("New remark", "Bob")),
            (Carol, new TestContact("Carol", "Carol")),
            (Dave, new TestContact(null, null))),
            completeness: RawGenerationCompleteness.Partial);
        await Assert.ThrowsAsync<IncompleteCanonicalCoverageException>(() =>
            new RebuildService(vault, archiveBPath, new FixedClock()).RebuildAsync(null, CancellationToken.None));
        await AssertArchivesEqualAsync(archiveA, archiveB);
    }

    // ---- fixture and measurement helpers -----------------------------------------------

    private static List<(string UserId, TestContact? Contact)> Contacts(
        params (string UserId, TestContact? Contact)[] rows) => [.. rows];

    private static async Task<RawGeneration> PublishGenerationAsync(
        RawVaultStore vault,
        string scratchRoot,
        DateTimeOffset capturedAt,
        IReadOnlyList<(string UserId, TestContact? Contact)> contacts,
        RawGenerationCompleteness completeness = RawGenerationCompleteness.Complete)
    {
        var dbRoot = Path.Combine(scratchRoot, "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbRoot);

        var contactRows = string.Join(Environment.NewLine, contacts.Select(row => row.Contact is null
            ? string.Empty
            : $"INSERT INTO {WeChat4Schema.ContactTable} VALUES ('{row.UserId}', {SqlText(row.Contact.Remark)}, {SqlText(row.Contact.Nickname)}, NULL, 1);"
                ).Where(sql => sql.Length > 0));

        BuildDb(Path.Combine(dbRoot, "session.db"), $"""
            CREATE TABLE {WeChat4Schema.SessionTable} (username TEXT PRIMARY KEY, sort_timestamp INTEGER, last_timestamp INTEGER, last_msg_type INTEGER, last_msg_sub_type INTEGER, summary TEXT);
            INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{DirectConversationId}', 1737244800, 1737244800, 1, 0, NULL);
            INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{GroupConversationId}', 1737244790, 1737244790, 1, 0, NULL);
            """);
        BuildDb(Path.Combine(dbRoot, "contact.db"), $"""
            CREATE TABLE {WeChat4Schema.ContactTable} (username TEXT PRIMARY KEY, remark TEXT, nick_name TEXT, alias TEXT, local_type INTEGER);
            {contactRows}
            """);

        var directTable = WeChat4Schema.MessageTableName(DirectConversationId);
        var groupTable = WeChat4Schema.MessageTableName(GroupConversationId);

        // message_0.db — the current rotation window: the direct conversation and the group's
        // newest rows. Ghost has a Name2Id entry but no contact row on purpose.
        BuildDb(Path.Combine(dbRoot, "message_0.db"), $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (1, '{DirectConversationId}');
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (2, '{Carol}');
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (3, '{Dave}');
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (4, '{Ghost}');
            CREATE TABLE "{directTable}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{directTable}" VALUES (1, 7001, 1, 1, 1736907601, 'direct hello', 0, NULL);
            CREATE TABLE "{groupTable}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{groupTable}" VALUES (1, 6201, 1, 2, 1789996801, 'group newest from carol', 0, NULL);
            INSERT INTO "{groupTable}" VALUES (2, 6202, 1, 4, 1789996802, 'group newest from ghost', 0, NULL);
            """);

        // message_1.db — the group's older rotation window (Issue #71): its rows stay in this
        // shard while the newest window continues in message_0.db; each row's sender resolves
        // through the Name2Id map of the shard the row was read from.
        BuildDb(Path.Combine(dbRoot, "message_1.db"), $"""
            CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (2, '{Carol}');
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (3, '{Dave}');
            INSERT INTO {WeChat4Schema.Name2IdTable} VALUES (4, '{Ghost}');
            CREATE TABLE "{groupTable}" (local_id INTEGER PRIMARY KEY, server_id INTEGER, local_type INTEGER, real_sender_id INTEGER, create_time INTEGER, message_content BLOB, WCDB_CT_message_content INTEGER, compress_content BLOB);
            INSERT INTO "{groupTable}" VALUES (1, 6101, 1, 2, 1727769601, 'group oldest from carol', 0, NULL);
            INSERT INTO "{groupTable}" VALUES (2, 6102, 1, 3, 1727769602, 'group oldest from dave', 0, NULL);
            """);

        var context = new RawGenerationContext
        {
            AccountId = AccountId,
            SourceProfileId = Profile,
            CaptureTime = capturedAt,
            CaptureAdapterFamily = WeChatCaptureAdapter.Family,
            CaptureAdapterVersion = "reader-1",
        };
        var previous = await vault.GetLatestGenerationAsync(AccountId, CancellationToken.None);
        var session = await vault.BeginGenerationAsync(context, CancellationToken.None);
        var artifacts = new List<RawArtifactDescriptor>();
        var coverage = new List<RawPartitionCoverage>();
        foreach (var (name, relative) in new[]
        {
            ("session.db", "session/session.db"),
            ("contact.db", "contact/contact.db"),
            ("message_0.db", "message/message_0.db"),
            ("message_1.db", "message/message_1.db"),
        })
        {
            await using var stream = File.OpenRead(Path.Combine(dbRoot, name));
            var descriptor = await session.WriteArtifactAsync(
                "source-database", name, stream, "sqlite", true,
                new Dictionary<string, string> { ["source_relative_path"] = relative },
                CancellationToken.None);
            artifacts.Add(descriptor);
            coverage.Add(new RawPartitionCoverage
            {
                PartitionId = relative,
                Status = RawPartitionStatus.Captured,
                SourceFingerprint = descriptor.Sha256,
                ArtifactSha256 = descriptor.Sha256,
            });
        }

        var manifest = new RawManifest
        {
            GenerationId = session.GenerationId,
            AccountId = AccountId,
            SourceProfileId = Profile,
            Source = new RawManifestSource
            {
                AdapterName = WeChatWindowsSourceAdapter.Name,
                AdapterVersion = "reader-1",
                SourceProductName = "WeChat for Windows",
                SourceVersion = "4.1.13.12",
            },
            Capture = new RawManifestCapture
            {
                CaptureTime = capturedAt,
                CaptureAdapterFamily = WeChatCaptureAdapter.Family,
                CaptureAdapterVersion = "reader-1",
                Mode = previous is null ? RawCaptureMode.Baseline : RawCaptureMode.Incremental,
                Completeness = completeness,
                ArtifactCount = artifacts.Count,
            },
            Artifacts = artifacts,
            Coverage = coverage,
            CaptureCheckpoint = new RawCaptureCheckpoint
            {
                Version = 1,
                GenerationId = session.GenerationId,
                CaptureAdapterFamily = WeChatCaptureAdapter.Family,
                CaptureAdapterVersion = "reader-1",
                PartitionFingerprints = coverage.ToDictionary(
                    c => c.PartitionId, c => c.SourceFingerprint!, StringComparer.Ordinal),
            },
            PreviousGenerationId = previous?.GenerationId,
        };
        var result = await session.PublishAsync(manifest, CancellationToken.None);
        await session.DisposeAsync();
        Directory.Delete(dbRoot, recursive: true);
        return result;
    }

    private static string SqlText(string? value) =>
        value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

    private static void BuildDb(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task ExportEverythingAsync(SqliteArchiveStore archive, string outputRoot)
    {
        var conversations = await archive.ListConversationsAsync(AccountId, CancellationToken.None);
        var exporter = new JsonlDatasetExporter(archive);
        var result = await exporter.ExportAsync(new ExportRequest
        {
            OutputDirectory = outputRoot,
            AccountId = AccountId,
            CreatedAt = PinnedExportAt,
            ConversationIds = conversations.Select(c => c.Id).OrderBy(id => id, StringComparer.Ordinal).ToList(),
        }, null, CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureReason ?? "the export failed without a reason");
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            using var stream = File.OpenRead(path);
            snapshot[relative] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        return snapshot;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIdentities(string exportRoot)
    {
        var yaml = File.ReadAllText(Path.Combine(exportRoot, "identities.yaml"));
        var document = new DeserializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build()
            .Deserialize<Dictionary<string, object>>(yaml);

        return ((Dictionary<object, object>)document["users"]).ToDictionary(
            entry => (string)entry.Key,
            entry => ((Dictionary<object, object>)entry.Value).ToDictionary(
                field => (string)field.Key,
                field => field.Value as string ?? string.Empty),
            StringComparer.Ordinal);
    }

    private static void EditIdentities(
        string exportRoot,
        Action<Dictionary<string, Dictionary<string, string>>> edit)
    {
        var path = Path.Combine(exportRoot, "identities.yaml");
        var users = ReadIdentities(exportRoot);
        edit(users);
        var yaml = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .Build()
            .Serialize(new Dictionary<string, object> { ["users"] = users });
        File.WriteAllText(path, yaml);
    }

    private static List<Dictionary<string, object?>> ReadJsonLines(string timelineFolder)
    {
        var lines = new List<Dictionary<string, object?>>();
        foreach (var file in Directory.EnumerateFiles(timelineFolder, "*.jsonl", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file).Where(l => l.Length > 0))
            {
                using var document = System.Text.Json.JsonDocument.Parse(line);
                lines.Add(ToDictionary(document.RootElement));
            }
        }

        return lines;

        static Dictionary<string, object?> ToDictionary(System.Text.Json.JsonElement element)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => property.Value.GetString(),
                    System.Text.Json.JsonValueKind.Number => property.Value.GetRawText(),
                    System.Text.Json.JsonValueKind.Null => null,
                    _ => property.Value.GetRawText(),
                };
            }

            return result;
        }
    }

    private static async Task AssertArchivesEqualAsync(SqliteArchiveStore expected, SqliteArchiveStore actual)
    {
        var expectedAccounts = await expected.ListAccountsAsync(CancellationToken.None);
        var actualAccounts = await actual.ListAccountsAsync(CancellationToken.None);
        Assert.Equal(
            expectedAccounts.Select(a => (a.Id, a.SourceProfileId, a.AdapterName, a.SourceVersion)),
            actualAccounts.Select(a => (a.Id, a.SourceProfileId, a.AdapterName, a.SourceVersion)));

        foreach (var account in expectedAccounts)
        {
            var expectedParticipants = await expected.ListParticipantsAsync(account.Id, CancellationToken.None);
            var actualParticipants = await actual.ListParticipantsAsync(account.Id, CancellationToken.None);
            Assert.Equal(
                expectedParticipants.Select(p => (p.Id, p.SourceParticipantId, p.LatestRemark, p.Nickname, p.Alias)),
                actualParticipants.Select(p => (p.Id, p.SourceParticipantId, p.LatestRemark, p.Nickname, p.Alias)));

            var expectedConversations = await expected.ListConversationsAsync(account.Id, CancellationToken.None);
            var actualConversations = await actual.ListConversationsAsync(account.Id, CancellationToken.None);
            Assert.Equal(
                expectedConversations.Select(c => c.Id),
                actualConversations.Select(c => c.Id));
            foreach (var conversation in expectedConversations)
            {
                var expectedMessages = await expected.ReadMessagesAsync(conversation.Id, CancellationToken.None);
                var actualMessages = await actual.ReadMessagesAsync(conversation.Id, CancellationToken.None);
                Assert.Equal(
                    expectedMessages.Select(m => (m.Id, m.SenderId, m.Text, m.OccurredAt, m.Source.SourcePartition,
                        m.Source.SourceType, m.Source.SourceSubtype, m.Source.SourceMessageId)),
                    actualMessages.Select(m => (m.Id, m.SenderId, m.Text, m.OccurredAt, m.Source.SourcePartition,
                        m.Source.SourceType, m.Source.SourceSubtype, m.Source.SourceMessageId)));
            }
        }
    }
}
