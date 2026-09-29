using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Infrastructure.WeChat.Compatibility;

namespace WeArchive.Tests.Support;

/// <summary>
/// One conversation the synthetic capture source exposes, and how its preserved evidence looks.
/// <para>
/// Defaults model a healthy conversation. <see cref="MessageTablePresent"/> = false models a
/// conversation whose message shard is missing while the session row still exists: under the
/// Issue #37 rule that is a Fatal source-coverage condition only when the generation cannot prove
/// its Required message evidence is complete, which is what
/// <see cref="MessageShardUnreadable"/> additionally models.
/// </para>
/// </summary>
internal sealed class SyntheticCaptureConversation
{
    public required string SourceConversationId { get; init; }

    public string Text { get; set; } = "synthetic message";

    /// <summary>Number of message rows written into this conversation's message shard.</summary>
    public int MessageCount { get; set; } = 1;

    /// <summary>When false, the conversation's message shard has no message table.</summary>
    public bool MessageTablePresent { get; set; } = true;

    /// <summary>
    /// When true, this conversation's table is written into a message shard that cannot be read as
    /// SQLite. That keeps the Fatal source-coverage condition available to tests now that a
    /// conversation provably absent from every successfully indexed shard is legitimately empty
    /// (Issue #37): a conversation must never be published as complete while required evidence
    /// could actually be missing.
    /// </summary>
    public bool MessageShardUnreadable { get; set; }
}

/// <summary>
/// The synthetic live source behind a conversation- or Collection-scope sync test. It reports one
/// account, and the live conversation surface of the same conversations its paired
/// <see cref="SyntheticWeChatCaptureAdapter"/> preserves, so selector resolution
/// (<c>sync --conversation</c>, <c>conversation show</c>) runs against a source that is consistent
/// with the captured evidence. The captured evidence — not this surface — remains what the ingest
/// path reads.
/// </summary>
internal sealed class SyntheticWeChatSourceAdapter : ISourceAdapter
{
    public const string ProfileId = "wxid_synthetic_account";

    public string AdapterName => WeChatWindowsSourceAdapter.Name;

    public string AdapterVersion => "0.1.0";

    /// <summary>When false, capture reports an unavailable source instead of publishing a generation.</summary>
    public bool IsAvailable { get; set; } = true;

    /// <summary>
    /// The conversations the live surface exposes. Tests mutate these same instances to model new
    /// or changed source content, so the live surface and the preserved evidence move together.
    /// </summary>
    public IReadOnlyList<SyntheticCaptureConversation> Conversations { get; set; } = [];

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SourceDescriptor
        {
            AdapterName = AdapterName,
            AdapterVersion = AdapterVersion,
            SourceVersion = "4.1.13.12",
            SourceProductName = "WeChat for Windows",
            IsAvailable = IsAvailable,
            UnavailableReason = IsAvailable ? null : "The synthetic source is not running.",
        });

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceAccount>>(
        [
            new SourceAccount
            {
                SourceProfileId = ProfileId,
                DisplayName = ProfileId,
                IsCurrent = true,
            },
        ]);

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceConversation>>(
        [
            .. Conversations.Select(conversation => new SourceConversation
            {
                SourceConversationId = conversation.SourceConversationId,
                Kind = SyntheticWeChatNaming.KindOf(conversation.SourceConversationId),
                PeerSourceUserId = SyntheticWeChatNaming.PeerOf(conversation.SourceConversationId),
                Title = conversation.SourceConversationId,
            }),
        ]);

    public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SourceParticipant>>([]);

    public Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken)
    {
        var conversation = Conversations.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceConversationId, sourceConversationId, StringComparison.Ordinal));
        if (conversation is null)
        {
            throw new KeyNotFoundException(
                $"The synthetic source exposes no conversation '{sourceConversationId}'.");
        }

        return Task.FromResult(new SourceConversationDetail
        {
            SourceConversationId = sourceConversationId,
            MessageCount = Math.Max(conversation.MessageCount, 1),
            ParticipantCount = 1,
        });
    }

    public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            "The synthetic source drives capture only; the captured evidence is what ingest reads.");
}

/// <summary>
/// A synthetic capture adapter that materializes the conversations under test as decrypted,
/// WeChat-4.x-shaped SQLite images inside a Raw Vault generation.
/// <para>
/// It declares the shipped WeChat capture adapter family/version and a 4.x source version so the
/// captured-source reader accepts the published generation (docs/ARCHITECTURE.md section 3.6.1).
/// Evidence is built from <see cref="Conversations"/>, which a test may mutate between runs to
/// model changed and unchanged source content and to model a later recovery of a failed member.
/// </para>
/// <para>
/// It also implements the shipped incremental capture path (Issue #25): each database image has a
/// deterministic source fingerprint over the SQL that materializes it, so an unchanged image is
/// reused from the verified predecessor generation exactly as the live WeChat adapter reuses an
/// unchanged partition. That keeps <c>capture_mode</c> meaningful in second-sync tests without a
/// live client or a database key.
/// </para>
/// </summary>
internal sealed class SyntheticWeChatCaptureAdapter : IIncrementalSourceCaptureAdapter, IDisposable
{
    private const string SourceDatabaseRole = "source-database";

    /// <summary>Sentinel SQL text marking an artifact that is deliberately not a database.</summary>
    private const string NotADatabaseMarker = "\0not-a-database\0";

    private readonly string _scratchRoot;
    private bool _disposed;

    public SyntheticWeChatCaptureAdapter(IEnumerable<SyntheticCaptureConversation> conversations)
    {
        Conversations = [.. conversations];
        _scratchRoot = Path.Combine(
            Path.GetTempPath(),
            "WeArchive-collection-capture",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public List<SyntheticCaptureConversation> Conversations { get; }

    /// <summary>When set, capture fails instead of publishing evidence.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// Extra coverage entries a test injects to model partitions outside the healthy baseline —
    /// known-unsupported, unclassified or unavailable evidence (Issue #51). They are appended to
    /// the capture result exactly as a real adapter records them; the capture checkpoint still
    /// covers only the captured/reused evidence (docs/RAW_VAULT.md section 4.3).
    /// </summary>
    public List<RawPartitionCoverage> ExtraCoverage { get; } = [];

    /// <summary>Diagnostics paired with <see cref="ExtraCoverage"/> — for example the
    /// <c>partition_unsupported</c> (info) and <c>partition_unclassified</c> (partial) codes the
    /// source-partition policy records (Issue #37).</summary>
    public List<RawManifestDiagnostic> ExtraDiagnostics { get; } = [];

    /// <summary>
    /// Overrides the completeness verdict the adapter reports, so a test can model a generation
    /// the capture policy publishes as partial (an unavailable or unclassified partition) instead
    /// of the healthy <c>complete</c> baseline.
    /// </summary>
    public RawGenerationCompleteness? CompletenessOverride { get; set; }

    public string CaptureAdapterFamily => WeChatCaptureAdapter.Family;

    /// <summary>
    /// Mutable so a test can reproduce an adapter version change, which invalidates the published
    /// capture checkpoint and makes the next capture widen to a full consistent snapshot
    /// (Issue #25 / docs/DEVELOPMENT.md section 10.3.1).
    /// </summary>
    public string CaptureAdapterVersion { get; set; } = WeChatCaptureAdapter.Version;

    public Task<SourceCaptureResult> CaptureAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken) =>
        CaptureCoreAsync(session, null, progress, cancellationToken);

    public Task<SourceCaptureResult> CaptureIncrementalAsync(
        string sourceProfileId,
        IRawGenerationSession session,
        RawGeneration previous,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken) =>
        CaptureCoreAsync(session, previous, progress, cancellationToken);

    private async Task<SourceCaptureResult> CaptureCoreAsync(
        IRawGenerationSession session,
        RawGeneration? previous,
        IProgress<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Failure is not null)
        {
            throw Failure;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CaptureProgress { Stage = CaptureStages.Snapshotting, Total = 3 });

        var readableShardSql = BuildMessageSql(Conversations.Where(c => !c.MessageShardUnreadable).ToList());
        var databases = new List<(string Name, string RelativePath, string Sql)>
        {
            ("session.db", "session/session.db", BuildSessionSql()),
            ("contact.db", "contact/contact.db", BuildContactSql()),
            ("message_0.db", "message/message_0.db", readableShardSql),
        };

        // A conversation whose shard cannot be read is preserved as an artifact that is not a
        // readable SQLite database, so the reader reports it as an unreadable shard instead of
        // silently treating the conversation as empty (see SyntheticCaptureConversation).
        var hasUnreadableShard = Conversations.Any(c => c.MessageShardUnreadable);
        if (hasUnreadableShard)
        {
            databases.Add(("message_1.db", "message/message_1.db", NotADatabaseMarker));
        }

        var prior = previous?.Manifest.Coverage
            .Where(entry => entry.Status is RawPartitionStatus.Captured or RawPartitionStatus.Reused)
            .ToDictionary(entry => entry.PartitionId, StringComparer.Ordinal);

        var artifacts = new List<RawArtifactDescriptor>();
        var coverage = new List<RawPartitionCoverage>();
        var diagnostics = new List<RawManifestDiagnostic>();
        for (var i = 0; i < databases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (name, relativePath, sql) = databases[i];
            var fingerprint = SyntheticWeChatNaming.Sha256Hex(Encoding.UTF8.GetBytes(sql));

            // An unchanged image is reused from the verified predecessor instead of being
            // reacquired, mirroring the live adapter's partition reuse (Issue #25).
            var reusable = FindReusableArtifact(previous, prior, relativePath, fingerprint, name);
            if (reusable is not null)
            {
                var reused = await session
                    .ReuseArtifactAsync(previous!, reusable, cancellationToken)
                    .ConfigureAwait(false);
                artifacts.Add(reused);
                coverage.Add(new RawPartitionCoverage
                {
                    PartitionId = relativePath,
                    Status = RawPartitionStatus.Reused,
                    SourceFingerprint = fingerprint,
                    ArtifactSha256 = reused.Sha256,
                });
                progress?.Report(new CaptureProgress
                {
                    Stage = CaptureStages.Snapshotting,
                    Processed = i + 1,
                    Total = databases.Count,
                });
                continue;
            }

            var path = Path.Combine(_scratchRoot, name);
            if (sql == NotADatabaseMarker)
            {
                await File.WriteAllBytesAsync(path, "this is not a sqlite database"u8.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                BuildDatabase(path, sql);
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var descriptor = await session
                .WriteArtifactAsync(
                    SourceDatabaseRole,
                    name,
                    stream,
                    sourceFormat: "sqlite",
                    isDecrypted: true,
                    metadata: new Dictionary<string, string>
                    {
                        ["synthetic"] = "true",
                        ["source_relative_path"] = relativePath,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            artifacts.Add(descriptor);
            coverage.Add(new RawPartitionCoverage
            {
                PartitionId = relativePath,
                Status = RawPartitionStatus.Captured,
                SourceFingerprint = fingerprint,
                ArtifactSha256 = descriptor.Sha256,
            });

            progress?.Report(new CaptureProgress
            {
                Stage = CaptureStages.Snapshotting,
                Processed = i + 1,
                Total = databases.Count,
            });
        }

        return new SourceCaptureResult
        {
            Artifacts = artifacts,
            Diagnostics = [.. diagnostics, .. ExtraDiagnostics],
            Coverage = [.. coverage, .. ExtraCoverage],
            Mode = previous is null ? RawCaptureMode.Baseline : RawCaptureMode.Incremental,
            Completeness = CompletenessOverride ?? RawGenerationCompleteness.Complete,
            SourceProductName = "WeChat for Windows",
            SourceVersion = "4.1.13.12",
        };
    }

    private static RawArtifactDescriptor? FindReusableArtifact(
        RawGeneration? previous,
        Dictionary<string, RawPartitionCoverage>? prior,
        string partitionId,
        string fingerprint,
        string name)
    {
        if (previous is null ||
            prior is null ||
            !prior.TryGetValue(partitionId, out var entry) ||
            !string.Equals(entry.SourceFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        return previous.Manifest.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Name, name, StringComparison.Ordinal));
    }

    private string BuildSessionSql()
    {
        var rows = string.Join(Environment.NewLine, Conversations.Select((conversation, index) =>
            $"INSERT INTO {WeChat4Schema.SessionTable} VALUES ('{conversation.SourceConversationId}', {1737244800 + index}, {1737244800 + index}, 1, 0, NULL);"));
        return $"""
            CREATE TABLE {WeChat4Schema.SessionTable} (
                username TEXT PRIMARY KEY,
                sort_timestamp INTEGER,
                last_timestamp INTEGER,
                last_msg_type INTEGER,
                last_msg_sub_type INTEGER,
                summary TEXT);
            {rows}
            """;
    }

    private string BuildContactSql()
    {
        var rows = string.Join(Environment.NewLine, Conversations.Select(conversation =>
            $"INSERT INTO {WeChat4Schema.ContactTable} VALUES ('{conversation.SourceConversationId}', 'Remark {conversation.SourceConversationId}', 'Nick', NULL, 1);"));
        return $"""
            CREATE TABLE {WeChat4Schema.ContactTable} (
                username TEXT PRIMARY KEY,
                remark TEXT,
                nick_name TEXT,
                alias TEXT,
                local_type INTEGER);
            {rows}
            CREATE TABLE stranger (username TEXT PRIMARY KEY, nick_name TEXT);
            """;
    }

    private string BuildMessageSql(IReadOnlyList<SyntheticCaptureConversation> shardConversations)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"CREATE TABLE {WeChat4Schema.Name2IdTable} (rowid INTEGER PRIMARY KEY, user_name TEXT);");

        for (var i = 0; i < shardConversations.Count; i++)
        {
            var conversation = shardConversations[i];
            builder.AppendLine(
                $"INSERT INTO {WeChat4Schema.Name2IdTable} VALUES ({i + 1}, '{conversation.SourceConversationId}');");
        }

        foreach (var conversation in shardConversations)
        {
            if (!conversation.MessageTablePresent)
            {
                // The session row exists but this shard does not hold the conversation's table: a
                // Fatal source-coverage condition unless the generation proves otherwise.
                continue;
            }

            var table = WeChat4Schema.MessageTableName(conversation.SourceConversationId);
            builder.AppendLine($"""
                CREATE TABLE "{table}" (
                    local_id INTEGER PRIMARY KEY,
                    server_id INTEGER,
                    local_type INTEGER,
                    real_sender_id INTEGER,
                    create_time INTEGER,
                    message_content BLOB,
                    WCDB_CT_message_content INTEGER,
                    compress_content BLOB);
                """);

            var rows = Enumerable.Range(1, Math.Max(conversation.MessageCount, 1)).Select(id =>
                $"INSERT INTO \"{table}\" VALUES ({id}, {7000 + id}, 1, 1, {1736907600 + id}, '{conversation.Text} {id}', 0, NULL);");
            foreach (var row in rows)
            {
                builder.AppendLine(row);
            }
        }

        return builder.ToString();
    }

    private static void BuildDatabase(string path, string sql)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_scratchRoot))
            {
                Directory.Delete(_scratchRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}

/// <summary>Helpers the Collection tests share for naming synthetic conversations.</summary>
internal static class SyntheticWeChatNaming
{
    /// <summary>A direct (one-to-one) conversation's upstream id.</summary>
    public static string DirectId(string suffix) => "wxid_" + suffix;

    /// <summary>A group conversation's upstream room id.</summary>
    public static string GroupId(string suffix) => suffix + "@chatroom";

    /// <summary>The conversation kind the shipped WeChat classifier derives for an upstream id.</summary>
    public static ConversationKind KindOf(string sourceConversationId) =>
        WeChat4Schema.ClassifyConversation(sourceConversationId);

    /// <summary>
    /// The peer identity a direct conversation reports, mirroring
    /// <c>WeChatWindowsSourceAdapter.ListConversationsAsync</c>; a group conversation has none.
    /// </summary>
    public static string? PeerOf(string sourceConversationId) =>
        KindOf(sourceConversationId) == ConversationKind.Direct ? sourceConversationId : null;

    /// <summary>
    /// The stable conversation id the source surface reports for an upstream id, mirroring
    /// <c>WeChatWindowsSourceAdapter.ListConversationsAsync</c> (a direct conversation's peer is its
    /// own upstream id; a group conversation has no peer).
    /// </summary>
    public static string StableConversationId(string accountId, string sourceConversationId) =>
        StableIds.Conversation(
            accountId,
            KindOf(sourceConversationId),
            sourceConversationId,
            PeerOf(sourceConversationId));

    /// <summary>The WeChat 4.x message-shard table name for a conversation.</summary>
    public static string MessageTableName(string sourceConversationId) =>
        WeChat4Schema.MessageTableName(sourceConversationId);

    /// <summary>A SHA-256 hex digest, used by tests that compare preserved file content.</summary>
    public static string Sha256Hex(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
