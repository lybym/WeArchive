using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;

namespace WeArchive.Infrastructure.Export;

/// <summary>
/// Phase 1 machine-oriented dataset exporter.
/// Behaviour is normative in docs/EXPORT_PRD.md.
/// <para>
/// It consumes normalized archive models only. Physical paths are derived from stable
/// IDs, never from remarks, nicknames or group titles, so a rename never invalidates a
/// Harness reference. Re-exporting a conversation rewrites its own partitions, which
/// makes duplicate logical records structurally impossible.
/// </para>
/// <para>
/// A re-export is crash-safe and recoverable: each conversation's timeline is written to a
/// sibling staging directory, and the root catalogs and manifest are written to sibling
/// temp files. The prior conversation directory is renamed to a sibling backup (not
/// deleted) and the staged replacement is moved into place; the backup is deleted only
/// once the new final is durable. A cancellation, I/O failure, process crash or power loss
/// therefore cannot destroy the last good dataset — a leftover backup is recovered (when
/// final is absent) or swept (when final is present) at the start of the next run.
/// docs/EXPORT_PRD.md section 3.2 and section 15.
/// </para>
/// </summary>
public class JsonlDatasetExporter(IArchiveStore archive) : IDatasetExporter
{
    public const string ExportSchemaVersion = "1.0";
    public const string MessageSchemaVersion = "1.0";

    // Conversation timelines stage under a sibling directory whose name starts with this
    // prefix so it can never collide with a stable conversation id (g_.../u_...) and so
    // leftover staging directories from a crashed run can be swept at the start of the
    // next export.
    private const string ConversationStagingPrefix = "wearchive-export-staging-";

    // During a commit the prior conversation directory is renamed to a sibling backup with
    // this infix (plus a unique suffix) rather than deleted, so a crash between the rename
    // and the move never loses the last good dataset; the backup is recovered or swept on
    // the next run.
    private const string ConversationBackupInfix = ".wearchive-backup-";

    // Root catalogs and the manifest stage under a sibling temp file with this suffix.
    private const string RootFileStagingSuffix = ".wearchive-export-staging";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // The dataset is plain text for machines and LLMs, never embedded in HTML, so the
        // relaxed encoder is correct here: CJK and punctuation stay readable and the files
        // stay small. Control characters and quotes are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));

    public string ExporterVersion { get; } =
        typeof(JsonlDatasetExporter).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public async Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var root = Path.GetFullPath(request.OutputDirectory);
        var createdAt = request.CreatedAt ?? DateTimeOffset.Now;
        var files = new List<ExportedFile>();
        var manifestFiles = new List<ManifestFile>();
        var manifestConversations = new List<ManifestConversation>();
        var conversationPaths = new List<string>();

        var recordCount = 0;
        var unknownCount = 0;
        var partialCount = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        RecoverAndSweepStaging(root);

        // Identity resolution is centralized here. Only the participants referenced by the
        // exported conversations are published, so the catalog stays small enough for a
        // consumer to load without pulling in the whole contact list.
        var account = (await _archive.ListAccountsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.Id, request.AccountId, StringComparison.Ordinal));

        var accountParticipants = await _archive
            .ListParticipantsAsync(request.AccountId, cancellationToken)
            .ConfigureAwait(false);

        var participantById = new Dictionary<string, ArchiveParticipant>(StringComparer.Ordinal);
        foreach (var participant in accountParticipants)
        {
            participantById[participant.Id] = participant;
        }

        var ledger = new Dictionary<string, IdentityEntry>(StringComparer.Ordinal);

        // Everything is staged first and committed only once every timeline, catalog and
        // the manifest are durable. On any failure the staging artifacts are discarded and
        // the previously-exported package is left untouched.
        var stagedConversations = new List<(string Staging, string Final)>();
        var stagedRootFiles = new List<(string Temp, string Final)>();

        try
        {
            foreach (var conversationId in request.ConversationIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var conversation = await _archive.GetConversationAsync(conversationId, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Conversation '{conversationId}' is not present in the archive. Import it before exporting.");

                var messages = await _archive.ReadMessagesAsync(conversationId, cancellationToken)
                    .ConfigureAwait(false);

                var relativeFolder = BuildConversationFolder(conversation);
                conversationPaths.Add(relativeFolder);

                var finalFolder = Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
                var stagingFolder = CreateStagingFolder(finalFolder);
                stagedConversations.Add((stagingFolder, finalFolder));

                var written = WriteTimeline(stagingFolder, relativeFolder, conversation.Id, messages, files, manifestFiles, cancellationToken);
                recordCount += written.Records;
                unknownCount += written.Unknown;
                partialCount += written.Partial;
                first = Min(first, written.First);
                last = Max(last, written.Last);

                manifestConversations.Add(new ManifestConversation
                {
                    ConversationId = conversation.Id,
                    Type = conversation.Kind.ToWireName(),
                    CurrentName = conversation.Title,
                    RecordCount = written.Records,
                    FirstMessageAt = written.First is null ? null : FormatTimestamp(written.First.Value),
                    LastMessageAt = written.Last is null ? null : FormatTimestamp(written.Last.Value),
                });

                RecordReferencedSenders(conversation, messages, ledger, participantById);

                progress?.Report(new ExportProgress
                {
                    Stage = "writing_timeline",
                    Processed = recordCount,
                    Total = recordCount,
                    ConversationId = conversationId,
                });
            }

            await WriteIdentitiesAsync(root, ledger, stagedRootFiles, cancellationToken).ConfigureAwait(false);
            files.Add(new ExportedFile
            {
                RelativePath = "identities.yaml",
                Kind = "identities",
                RecordCount = ledger.Count,
            });
            manifestFiles.Add(new ManifestFile
            {
                Path = "identities.yaml",
                Kind = "identities",
                RecordCount = ledger.Count,
            });

            await WriteConversationsAsync(root, manifestConversations, stagedRootFiles, cancellationToken).ConfigureAwait(false);
            files.Add(new ExportedFile
            {
                RelativePath = "conversations.yaml",
                Kind = "conversations",
                RecordCount = manifestConversations.Count,
            });
            manifestFiles.Add(new ManifestFile
            {
                Path = "conversations.yaml",
                Kind = "conversations",
                RecordCount = manifestConversations.Count,
            });

            WriteCollections(root, stagedRootFiles);
            files.Add(new ExportedFile { RelativePath = "collections.yaml", Kind = "collections" });
            manifestFiles.Add(new ManifestFile { Path = "collections.yaml", Kind = "collections" });

            var manifest = new ExportManifest
            {
                ExportSchemaVersion = ExportSchemaVersion,
                MessageSchemaVersion = MessageSchemaVersion,
                ExporterVersion = ExporterVersion,
                CreatedAt = FormatTimestamp(createdAt),
                SourceAccountId = request.AccountId,
                SourceAdapter = account?.AdapterName,
                SourceVersion = account?.SourceVersion,
                ConversationIds = [.. request.ConversationIds],
                TimeRange = new ManifestTimeRange
                {
                    FirstMessageAt = first is null ? null : FormatTimestamp(first.Value),
                    LastMessageAt = last is null ? null : FormatTimestamp(last.Value),
                },
                RecordCount = recordCount,
                UnknownCount = unknownCount,
                PartialCount = partialCount,
                UnsupportedCount = unknownCount,
                Files = manifestFiles,
                Conversations = manifestConversations,
                Diagnostics = [.. request.Diagnostics.Select(ToManifestDiagnostic)],
            };

            var manifestPath = Path.Combine(root, "manifest.json");
            var manifestTemp = manifestPath + RootFileStagingSuffix;
            await DurableWriteFileAsync(
                manifestTemp,
                JsonSerializer.Serialize(manifest, ManifestOptions),
                cancellationToken).ConfigureAwait(false);
            stagedRootFiles.Add((manifestTemp, manifestPath));

            // Everything is durable. Commit: swap the staged conversation directories in
            // (each establishes its own durability barrier before deleting its backup), then
            // rename the staged root files over their originals. The manifest is renamed
            // last so a consumer that loads it never sees references to files that have not
            // yet been swapped.
            foreach (var (staging, final) in stagedConversations)
            {
                CommitConversation(staging, final);
            }

            foreach (var (temp, final) in stagedRootFiles)
            {
                File.Move(temp, final, overwrite: true);
            }

            // Make the root catalog/manifest rename metadata durable too.
            NativeMethods.FsyncDirectory(root);
        }
        catch (Exception)
        {
            // Discard every staging artifact. The previously-exported package was never
            // touched (its conversation directories are only deleted inside
            // CommitConversation, which runs last, and its root files are only overwritten
            // by the moves above).
            foreach (var (staging, _) in stagedConversations)
            {
                TryDeleteDirectory(staging);
            }

            foreach (var (temp, _) in stagedRootFiles)
            {
                TryDeleteFile(temp);
            }

            throw;
        }

        return new ExportResult
        {
            Succeeded = true,
            OutputDirectory = root,
            Files = files,
            ConversationIds = [.. request.ConversationIds],
            RecordCount = recordCount,
            UnknownCount = unknownCount,
            PartialCount = partialCount,
            FirstMessageAt = first,
            LastMessageAt = last,
            ConversationPaths = conversationPaths,
        };
    }

    private static (int Records, int Unknown, int Partial, DateTimeOffset? First, DateTimeOffset? Last) WriteTimeline(
        string absoluteFolder,
        string relativeFolder,
        string conversationId,
        IReadOnlyList<CanonicalMessage> messages,
        List<ExportedFile> files,
        List<ManifestFile> manifestFiles,
        CancellationToken cancellationToken)
    {
        var records = 0;
        var unknown = 0;
        var partial = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;

        // Physical partitioning: conversation / year / month. Ordering is already canonical
        // (time, then upstream order key, then stable id) from the archive read.
        foreach (var group in messages.GroupBy(m => (m.OccurredAt.Year, m.OccurredAt.Month)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = string.Create(
                CultureInfo.InvariantCulture,
                $"{relativeFolder}/{group.Key.Year}/{group.Key.Year}-{group.Key.Month:00}.jsonl");
            var absolutePath = Path.Combine(
                absoluteFolder,
                group.Key.Year.ToString(CultureInfo.InvariantCulture),
                $"{group.Key.Year}-{group.Key.Month:00}.jsonl");

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

            // One JSON value per line: a writer is scoped to a single message so the file is
            // JSON Lines rather than one large JSON array. docs/EXPORT_PRD.md section 9.
            var buffer = new ArrayBufferWriter<byte>(8192);
            using (var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var message in group)
                {
                    buffer.Clear();
                    using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
                    {
                        WriteMessage(writer, message);
                    }

                    stream.Write(buffer.WrittenSpan);
                    stream.WriteByte((byte)'\n');

                    records++;
                    if (message.Type == CanonicalMessageType.Unknown)
                    {
                        unknown++;
                    }

                    if (message.IsPartial)
                    {
                        partial++;
                    }

                    first = first is null || message.OccurredAt < first ? message.OccurredAt : first;
                    last = last is null || message.OccurredAt > last ? message.OccurredAt : last;
                }

                // fsync the partition contents to media so the staged tree is durable before
                // the commit moves it into place and deletes the prior-good backup.
                stream.Flush(true);
            }

            files.Add(new ExportedFile
            {
                RelativePath = relativePath,
                Kind = "timeline",
                RecordCount = group.Count(),
                SizeBytes = new FileInfo(absolutePath).Length,
            });
            manifestFiles.Add(new ManifestFile
            {
                Path = relativePath,
                Kind = "timeline",
                ConversationId = conversationId,
                Year = group.Key.Year,
                Month = group.Key.Month,
                RecordCount = group.Count(),
            });
        }

        return (records, unknown, partial, first, last);
    }

    private static void WriteMessage(Utf8JsonWriter writer, CanonicalMessage message)
    {
        writer.WriteStartObject();
        writer.WriteString("id", message.Id);
        writer.WriteString("conversation_id", message.ConversationId);
        if (message.SenderId is null)
        {
            writer.WriteNull("sender_id");
        }
        else
        {
            writer.WriteString("sender_id", message.SenderId);
        }

        writer.WriteString("time", FormatTimestamp(message.OccurredAt));
        writer.WriteString("type", message.Type.ToWireName());
        writer.WriteString("text", message.Text);

        if (message.ReplyTo is null)
        {
            writer.WriteNull("reply_to");
        }
        else
        {
            writer.WriteStartObject("reply_to");
            WriteNullableString(writer, "message_id", message.ReplyTo.MessageId);
            WriteNullableString(writer, "sender_id", message.ReplyTo.SenderId);
            WriteNullableString(writer, "sender_name", message.ReplyTo.SenderName);
            WriteNullableString(writer, "text", message.ReplyTo.Text);
            if (message.ReplyTo.OccurredAt is null)
            {
                writer.WriteNull("time");
            }
            else
            {
                writer.WriteString("time", FormatTimestamp(message.ReplyTo.OccurredAt.Value));
            }

            writer.WriteEndObject();
        }

        if (message.Payload is null)
        {
            writer.WriteNull("payload");
        }
        else
        {
            writer.WritePropertyName("payload");
            message.Payload.WriteTo(writer);
        }

        writer.WriteStartObject("source");
        WriteNullableString(writer, "source_message_id", message.Source.SourceMessageId);
        WriteNullableString(writer, "source_type", message.Source.SourceType);
        WriteNullableString(writer, "source_subtype", message.Source.SourceSubtype);
        WriteNullableString(writer, "source_partition", message.Source.SourcePartition);
        WriteNullableString(writer, "source_order_key", message.Source.SourceOrderKey);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>
    /// Stable physical folder. Direct chats live under <c>chats/direct</c> and groups
    /// under <c>chats/groups</c>, both keyed by stable ID only.
    /// </summary>
    private static string BuildConversationFolder(ArchiveConversation conversation)
    {
        var bucket = conversation.Kind == ConversationKind.Group ? "groups" : "direct";
        return $"chats/{bucket}/{conversation.Id}";
    }

    /// <summary>
    /// Publishes exactly the identities that appear in the exported timelines. A sender that
    /// has no archived participant record is still published by stable id with empty
    /// metadata rather than being dropped or given a fabricated name.
    /// </summary>
    private static void RecordReferencedSenders(
        ArchiveConversation conversation,
        IReadOnlyList<CanonicalMessage> messages,
        Dictionary<string, IdentityEntry> ledger,
        IReadOnlyDictionary<string, ArchiveParticipant> participantById)
    {
        foreach (var message in messages)
        {
            Add(message.SenderId);
            Add(message.ReplyTo?.SenderId);
        }

        if (conversation.Kind == ConversationKind.Direct)
        {
            // The peer may not appear as a sender in a very short export.
            Add(conversation.PeerParticipantId);
        }

        void Add(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || ledger.ContainsKey(id))
            {
                return;
            }

            ledger[id] = participantById.TryGetValue(id, out var participant)
                ? new IdentityEntry
                {
                    SourceUserId = participant.SourceParticipantId,
                    Remark = participant.LatestRemark ?? string.Empty,
                    Nickname = participant.Nickname ?? string.Empty,
                    DisplayName = participant.ResolveDisplayName(),
                }
                : new IdentityEntry();
        }
    }

    private async Task WriteIdentitiesAsync(
        string root,
        Dictionary<string, IdentityEntry> ledger,
        List<(string Temp, string Final)> staged,
        CancellationToken cancellationToken)
    {
        var final = Path.Combine(root, "identities.yaml");
        var temp = final + RootFileStagingSuffix;
        var existing = File.Exists(final)
            ? YamlCatalogs.Deserialize<IdentityDocument>(await File.ReadAllTextAsync(final, cancellationToken).ConfigureAwait(false))
            : null;

        foreach (var (id, entry) in ledger)
        {
            if (existing?.Users.TryGetValue(id, out var previous) == true)
            {
                // Documented merge policy: the override is user-owned and always wins.
                entry.DisplayNameOverride = previous.DisplayNameOverride ?? string.Empty;
            }
        }

        // A user-maintained override for an identity that this export does not reference is
        // still carried across, so manual corrections are never silently discarded.
        if (existing is not null)
        {
            foreach (var (id, previous) in existing.Users)
            {
                if (!string.IsNullOrWhiteSpace(previous.DisplayNameOverride) && !ledger.ContainsKey(id))
                {
                    ledger[id] = previous;
                }
            }
        }

        var document = new IdentityDocument();
        foreach (var (id, entry) in ledger.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            document.Users[id] = entry;
        }

        await DurableWriteFileAsync(
            temp,
            YamlCatalogs.Serialize(document),
            cancellationToken).ConfigureAwait(false);
        staged.Add((temp, final));
    }

    private static async Task WriteConversationsAsync(
        string root,
        IReadOnlyList<ManifestConversation> conversations,
        List<(string Temp, string Final)> staged,
        CancellationToken cancellationToken)
    {
        var final = Path.Combine(root, "conversations.yaml");
        var temp = final + RootFileStagingSuffix;
        var existing = File.Exists(final)
            ? YamlCatalogs.Deserialize<ConversationDocument>(await File.ReadAllTextAsync(final, cancellationToken).ConfigureAwait(false))
            : null;

        var document = new ConversationDocument();
        foreach (var conversation in conversations)
        {
            var alias = string.Empty;
            if (existing?.Conversations.TryGetValue(conversation.ConversationId, out var previous) == true)
            {
                alias = previous.Alias ?? string.Empty;
            }

            document.Conversations[conversation.ConversationId] = new ConversationEntry
            {
                Type = conversation.Type,
                CurrentName = conversation.CurrentName ?? string.Empty,
                Alias = alias,
                UserId = conversation.Type == "direct" ? conversation.ConversationId : null,
                FirstMessageAt = conversation.FirstMessageAt,
                LastMessageAt = conversation.LastMessageAt,
            };
        }

        await DurableWriteFileAsync(
            temp,
            YamlCatalogs.Serialize(document),
            cancellationToken).ConfigureAwait(false);
        staged.Add((temp, final));
    }

    private static void WriteCollections(string root, List<(string Temp, string Final)> staged)
    {
        var final = Path.Combine(root, "collections.yaml");
        // collections.yaml is user-maintained; an export must never discard it.
        if (File.Exists(final))
        {
            return;
        }

        var temp = final + RootFileStagingSuffix;
        DurableWriteFile(temp, YamlCatalogs.Serialize(new CollectionDocument()));
        staged.Add((temp, final));
    }

    /// <summary>
    /// Creates a sibling staging directory for one conversation's timeline. It is on the
    /// same volume as the final directory so <see cref="CommitConversation"/> can swap it
    /// in with a rename rather than a cross-volume copy.
    /// </summary>
    private static string CreateStagingFolder(string finalFolder)
    {
        var parent = Path.GetDirectoryName(finalFolder)!;
        var staging = Path.Combine(parent, ConversationStagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>
    /// Swaps a staged conversation directory into its final path using a durable, recoverable
    /// protocol so a crash, power loss or move failure can never destroy the last good dataset:
    /// <list type="number">
    /// <item>rename the prior <paramref name="final"/> directory to a uniquely named backup
    /// (an atomic directory rename, never a recursive delete);</item>
    /// <item>move <paramref name="staging"/> into <paramref name="final"/>;</item>
    /// <item><see cref="DurableCommit"/> — flush the new tree's file contents (already flushed
    /// at write time) and its directory metadata to media, establishing that the replacement
    /// is durable;</item>
    /// <item>delete the backup only after that durability barrier succeeds.</item>
    /// </list>
    /// If the move or the durability barrier fails, the catch discards the not-yet-durable new
    /// final and restores the prior dataset from the backup. A crash between steps leaves the
    /// backup on disk; <see cref="RecoverAndSweepStaging"/> restores it on the next run.
    /// docs/EXPORT_PRD.md sections 3.2 and 15.
    /// </summary>
    private void CommitConversation(string staging, string final)
    {
        var backup = final + ConversationBackupInfix + Guid.NewGuid().ToString("N");
        var movedToBackup = false;
        try
        {
            if (Directory.Exists(final))
            {
                // Preserve the prior dataset in a sibling backup instead of deleting it.
                Directory.Move(final, backup);
                movedToBackup = true;
            }

            Directory.Move(staging, final);

            // The replacement is in place but not yet durable. Establish that the new data and
            // directory metadata are on media before deleting the prior-good backup, so a power
            // loss after this point cannot leave incomplete new data with no old data left.
            DurableCommit(final);
        }
        catch (Exception)
        {
            // The replacement is not durably committed. Restore the last good dataset from the
            // backup: discard the (possibly not-yet-durable) new final, then rename the backup
            // back into place. The staged directory is discarded by the caller's catch.
            if (movedToBackup && Directory.Exists(backup))
            {
                if (Directory.Exists(final))
                {
                    TryDeleteDirectory(final);
                }

                TryMoveDirectory(backup, final);
            }

            throw;
        }

        // The new final is durably committed. Safe to delete the prior backup (best-effort; a
        // leftover is swept on the next run).
        if (movedToBackup)
        {
            TryDeleteDirectory(backup);
        }
    }

    /// <summary>
    /// The durability barrier of the export commit: ensures the new conversation tree's file
    /// contents and directory metadata are on media before the prior-good backup is deleted.
    /// Override (e.g. to throw) in tests to fault-inject the boundary after the replacement
    /// move and before backup deletion. docs/EXPORT_PRD.md sections 3.2 and 15.
    /// </summary>
    protected internal virtual void DurableCommit(string directory)
    {
        // File contents were fsynced at write time (WriteTimeline / DurableWriteFile); this
        // flushes the directory metadata (the rename and file entries) to media.
        NativeMethods.FsyncDirectory(directory);
    }

    /// <summary>
    /// Writes a catalog/manifest file and flushes its data to media (the file-content half of
    /// the durability barrier) so a later atomic rename over durable data.
    /// </summary>
    private static async Task DurableWriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(true); // fsync the file contents to media
    }

    private static void DurableWriteFile(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true); // fsync the file contents to media
    }

    /// <summary>
    /// Sweeps leftover staging directories and recovers leftover backups from a previously
    /// crashed export. Only the immediate children of the two conversation buckets are scanned,
    /// so this is bounded by the number of conversations, not the whole export tree. A backup
    /// whose final directory is absent is recovered (renamed back to the final path). A backup
    /// whose final is also present means a commit crashed after the replacement move but before
    /// the backup was deleted; since the new final's durability cannot be confirmed post-hoc,
    /// the last good dataset is restored from the backup (the new final is discarded).
    /// </summary>
    private static void RecoverAndSweepStaging(string root)
    {
        foreach (var bucket in new[]
        {
            Path.Combine(root, "chats", "direct"),
            Path.Combine(root, "chats", "groups"),
        })
        {
            if (!Directory.Exists(bucket))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(bucket))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(ConversationStagingPrefix, StringComparison.Ordinal))
                {
                    // An orphaned staging directory is always partial new data: discard it.
                    TryDeleteDirectory(dir);
                    continue;
                }

                var backupIndex = name.IndexOf(ConversationBackupInfix, StringComparison.Ordinal);
                if (backupIndex <= 0)
                {
                    continue;
                }

                var finalName = name[..backupIndex];
                var final = Path.Combine(bucket, finalName);
                if (Directory.Exists(final))
                {
                    // A commit crashed after the replacement move. The new final may not be
                    // durably committed, so restore the last good dataset from the backup:
                    // discard the new final, then rename the backup back into place.
                    TryDeleteDirectory(final);
                    TryMoveDirectory(dir, final);
                }
                else
                {
                    // A crash left final absent but the prior data is in the backup:
                    // recover it so the last good dataset is not lost.
                    TryMoveDirectory(dir, final);
                }
            }
        }
    }

    private static void TryMoveDirectory(string source, string destination)
    {
        try
        {
            if (Directory.Exists(source) && !Directory.Exists(destination))
            {
                Directory.Move(source, destination);
            }
        }
        catch (IOException)
        {
            // Leave it; tried again on the next run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover staging directory does not corrupt the export
            // and is swept on the next run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static ManifestDiagnostic ToManifestDiagnostic(ImportDiagnostic diagnostic) => new()
    {
        Severity = diagnostic.Severity.ToString(),
        Code = diagnostic.Code,
        Message = diagnostic.Message,
        Count = diagnostic.Count,
        SourceType = diagnostic.SourceType,
        SourceSubtype = diagnostic.SourceSubtype,
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
