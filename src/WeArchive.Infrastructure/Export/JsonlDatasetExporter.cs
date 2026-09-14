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
/// A re-export is transactional and process-crash recoverable (the SQLite archive is the
/// system of record; the export is a derived, regenerable dataset). Each conversation's
/// timeline is staged to a sibling directory and the root catalogs and manifest to sibling
/// temp files, fully written before publication. The prior conversation directory is renamed
/// to a sibling backup (not deleted) and the staged replacement moved into place; the prior
/// root files are likewise backed up and their replacements moved over them (manifest last).
/// Backups are kept until a commit marker records that publication completed. A cancellation,
/// handled exception or I/O failure restores the in-progress backups and removes any newly
/// introduced path, so it does not destroy the last successfully published dataset. After a
/// process crash, the next run cleans staging and uses the marker to converge: a leftover
/// backup is swept (the new data is kept) when the marker is present, or restored (and
/// journaled new paths removed) when it is absent. Phase 1 does not guarantee power-loss or
/// storage-device durability. docs/EXPORT_PRD.md section 3.2 and section 15.
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

    // During a commit the prior root catalog/manifest file is renamed to a sibling backup with
    // this infix (plus a unique suffix) rather than overwritten, so a commit failure can restore
    // the prior package; the backup is restored or swept on the next run.
    private const string RootFileBackupInfix = ".wearchive-rootbackup-";

    // A marker written once publication completes (all conversations and root files are in place,
    // manifest last). Its presence proves the prior export's publication completed, so a leftover
    // backup is swept (the new data is kept) rather than restored; its absence means publication
    // did not complete, so leftovers are restored and journaled new paths removed, converging to
    // the last successfully published dataset. It is deleted once every backup has been cleaned
    // up. Phase 1 does not require a global root durability barrier (see docs/EXPORT_PRD.md 3.2):
    // the marker is an application-level transaction-completion signal.
    private const string CommitMarkerName = ".wearchive-commit-complete";

    // A journal of paths newly introduced by an in-progress publication (a conversation or root
    // file whose prior state was absence, so it has no sibling backup). Written before the
    // publication moves; on marker-absent crash recovery the listed paths are removed so the
    // export converges to the prior complete dataset. Deleted on success.
    private const string TransactionJournalName = ".wearchive-transaction";

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

        // Everything is staged first and published only once every timeline, catalog and the
        // manifest are fully written. On any failure the staging artifacts are discarded and the
        // previously-published package is left untouched.
        var stagedConversations = new List<(string Staging, string Final)>();
        var stagedRootFiles = new List<(string Temp, string Final)>();
        // Backups created during the commit (null when the prior did not exist, e.g. a new
        // conversation or a first-export root file). Declared here so the catch can roll them
        // back to the prior package even when the failure is inside the commit phase.
        var conversationBackups = new List<(string? Backup, string Final)>();
        var rootBackups = new List<(string? Backup, string Final)>();

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

            // Everything is staged. Publish as a single transaction: swap each conversation
            // directory in (its prior is renamed to a sibling backup, the replacement moved into
            // place; the backup survives until publication completes), then swap the root files
            // in (prior renamed to sibling backups, the replacement moved over; manifest last).
            // A commit marker records publication completion; only then are the backups deleted.
            // On any failure before the marker, every conversation and root backup is restored
            // and any newly introduced path (recorded in the transaction journal) is removed, so
            // the previously-published dataset is left untouched. Phase 1 does not require a
            // global root durability barrier (see docs/EXPORT_PRD.md 3.2).
            WriteTransactionJournal(root, stagedConversations, stagedRootFiles);
            foreach (var (staging, final) in stagedConversations)
            {
                var backup = CommitConversation(staging, final);
                conversationBackups.Add((backup, final));
            }

            foreach (var (temp, final) in stagedRootFiles)
            {
                string? backup = null;
                if (File.Exists(final))
                {
                    backup = final + RootFileBackupInfix + Guid.NewGuid().ToString("N");
                    File.Move(final, backup);
                }

                File.Move(temp, final);
                rootBackups.Add((backup, final));
            }

            // Publication is complete. The marker is the application-level transaction-completion
            // signal used by crash recovery (no global root durability barrier is required).
            DurableCommitRoot(root);
            WriteCommitMarker(root);
        }
        catch (Exception)
        {
            // Roll back to the prior package: restore every committed conversation and root
            // backup, and discard a new conversation/root file whose prior did not exist.
            foreach (var (backup, final) in conversationBackups)
            {
                RestoreConversationBackup(backup, final);
            }

            foreach (var (backup, final) in rootBackups)
            {
                RestoreRootFileBackup(backup, final);
            }

            foreach (var (staging, _) in stagedConversations)
            {
                TryDeleteDirectory(staging);
            }

            foreach (var (temp, _) in stagedRootFiles)
            {
                TryDeleteFile(temp);
            }

            TryDeleteFile(CommitMarkerPath(root));
            TryDeleteFile(TransactionJournalPath(root));
            throw;
        }

        // Publication completed. Delete the backups (best-effort). The transaction journal is
        // no longer needed; the marker is kept only while any backup lingers so the next run
        // sweeps it (committed) rather than restoring it, and is removed once everything is clean.
        TryDeleteFile(TransactionJournalPath(root));
        var allBackupsCleaned = true;
        foreach (var (backup, _) in conversationBackups)
        {
            if (backup is null)
            {
                continue;
            }

            TryDeleteDirectory(backup);
            if (Directory.Exists(backup))
            {
                allBackupsCleaned = false;
            }
        }

        foreach (var (backup, _) in rootBackups)
        {
            if (backup is null)
            {
                continue;
            }

            TryDeleteFile(backup);
            if (File.Exists(backup))
            {
                allBackupsCleaned = false;
            }
        }

        if (allBackupsCleaned)
        {
            TryDeleteFile(CommitMarkerPath(root));
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

                // Drain the writer so the staged partition is fully written before the commit
                // moves it into place. Phase 1 does not guarantee power-loss durability.
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
    /// Swaps a staged conversation directory into its final path using a recoverable protocol so
    /// a move failure or process crash does not destroy the last successfully published dataset:
    /// <list type="number">
    /// <item>rename the prior <paramref name="final"/> directory to a uniquely named backup
    /// (an atomic directory rename, never a recursive delete);</item>
    /// <item>move <paramref name="staging"/> into <paramref name="final"/>;</item>
    /// <item>keep the backup until publication completes (the commit marker), not just until the
    /// per-conversation move, so a later failure in the same export can still roll this
    /// conversation back;</item>
    /// <item>delete the backup only after the transaction completes.</item>
    /// </list>
    /// If the move fails, the catch discards the not-yet-published new final and restores the
    /// prior dataset from the backup. A crash between steps leaves the backup on disk;
    /// <see cref="RecoverAndSweepStaging"/> restores it (or sweeps it, when the marker shows the
    /// publication completed) on the next run. Phase 1 does not guarantee power-loss or
    /// storage-device durability (see docs/EXPORT_PRD.md 3.2 and 15).
    /// </summary>
    private string? CommitConversation(string staging, string final)
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

            // The replacement is in place. The backup is NOT deleted here: it survives until the
            // caller's transaction completes (the commit marker), so a later failure in the same
            // export can still roll this conversation back to the prior dataset.
            DurableCommit(final);
        }
        catch (Exception)
        {
            // The replacement is not yet published. Restore the last good dataset from the
            // backup: discard the not-yet-published new final, then rename the backup back into
            // place. The staged directory is discarded by the caller's catch.
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

        // The replacement is published. The backup persists until the caller's transaction
        // completes (the commit marker); it is deleted on success or restored on a later failure.
        return movedToBackup ? backup : null;
    }

    /// <summary>
    /// A post-move checkpoint for one conversation's publication, and the fault-injection seam for
    /// it. Phase 1 does not fsync directory metadata or guarantee power-loss durability (see
    /// docs/EXPORT_PRD.md 3.2); the conversation's file contents are flushed at write time and
    /// the move is the application-level publication. Override (e.g. to throw) in tests to
    /// fault-inject a failure after the replacement move and before the transaction completes,
    /// proving the in-process rollback restores the prior-good backup.
    /// </summary>
    protected internal virtual void DurableCommit(string directory)
    {
        // Phase 1 does not require a directory-metadata durability barrier.
    }

    /// <summary>
    /// A post-publication checkpoint (after the conversations and root files are in place, before
    /// the commit marker) and the fault-injection seam for the whole transaction. Phase 1 does not
    /// fsync the root directory or guarantee power-loss durability (see docs/EXPORT_PRD.md 3.2).
    /// Override (e.g. to throw) in tests to fault-inject a failure after the replacements and before
    /// the marker, proving the in-process rollback restores the entire prior package.
    /// </summary>
    protected internal virtual void DurableCommitRoot(string root)
    {
        // Phase 1 does not require a global root durability barrier.
    }

    /// <summary>
    /// The commit marker path under <paramref name="root"/>.
    /// </summary>
    private static string CommitMarkerPath(string root) => Path.Combine(root, CommitMarkerName);

    /// <summary>
    /// Writes the commit marker whose presence records that publication completed, so a leftover
    /// backup on the next run is swept (the new data is kept) rather than restored (interrupted).
    /// Phase 1 does not fsync the marker or guarantee power-loss durability (see
    /// docs/EXPORT_PRD.md 3.2); the marker is an application-level transaction-completion signal
    /// read by process-crash recovery. It is removed once every backup has been cleaned up.
    /// </summary>
    private static void WriteCommitMarker(string root)
    {
        DurableWriteFile(CommitMarkerPath(root), string.Empty);
    }

    /// <summary>
    /// The transaction journal path under <paramref name="root"/> (lists newly introduced paths
    /// so marker-absent crash recovery can remove them).
    /// </summary>
    private static string TransactionJournalPath(string root) =>
        Path.Combine(root, TransactionJournalName);

    /// <summary>
    /// Writes the transaction journal before any publication move: the relative paths of
    /// conversations and root files whose prior state was absence (no sibling backup). On a
    /// marker-absent crash recovery these paths are removed so the export converges to the prior
    /// complete dataset.
    /// </summary>
    private static void WriteTransactionJournal(
        string root,
        List<(string Staging, string Final)> stagedConversations,
        List<(string Temp, string Final)> stagedRootFiles)
    {
        var newFinals = new List<string>();
        foreach (var (_, final) in stagedConversations)
        {
            if (!Directory.Exists(final))
            {
                newFinals.Add(Path.GetRelativePath(root, final));
            }
        }

        foreach (var (_, final) in stagedRootFiles)
        {
            if (!File.Exists(final))
            {
                newFinals.Add(Path.GetRelativePath(root, final));
            }
        }

        File.WriteAllLines(TransactionJournalPath(root), newFinals);
    }

    /// <summary>
    /// Reads the transaction journal paths (relative to <paramref name="root"/>), or an empty
    /// list when no journal exists.
    /// </summary>
    private static List<string> ReadTransactionJournal(string root)
    {
        var path = TransactionJournalPath(root);
        if (!File.Exists(path))
        {
            return [];
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList();
    }

    /// <summary>
    /// Writes a catalog/manifest file, flushes the writer's buffer and closes it, so the staged
    /// output is fully written before publication (Phase 1 does not guarantee power-loss
    /// durability; see docs/EXPORT_PRD.md 3.2).
    /// </summary>
    private static async Task DurableWriteFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        stream.Flush(true); // drain the buffer so the staged output is fully written
    }

    private static void DurableWriteFile(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true); // drain the buffer so the staged output is fully written
    }

    /// <summary>
    /// Sweeps leftover staging and recovers leftover backups from a previously crashed export.
    /// The commit marker distinguishes the two crash states: if it is present, the prior
    /// publication completed, so a leftover backup is a post-publication cleanup leftover and is
    /// swept (the new data is kept); if it is absent, the prior publication did not complete, so a
    /// leftover backup is restored over its replacement, and any newly introduced path recorded in
    /// the transaction journal (one with no prior backup) is removed. Conversation backups live in
    /// the two conversation buckets; root-file backups live in the export root. Orphaned staging
    /// directories are always discarded. Phase 1 does not require a global root durability barrier
    /// (see docs/EXPORT_PRD.md 3.2). The marker and journal are removed once they have been used
    /// and every committed leftover has been swept, so a crash in this export is not masked as a
    /// completed prior publication.
    /// </summary>
    private static void RecoverAndSweepStaging(string root)
    {
        var committed = File.Exists(CommitMarkerPath(root));
        var allCommittedBackupsSwept = true;

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
                if (committed && Directory.Exists(final))
                {
                    // The prior publication completed; this leftover backup is a post-publication
                    // cleanup leftover. The new final is the committed timeline, so sweep it.
                    TryDeleteDirectory(dir);
                    if (Directory.Exists(dir))
                    {
                        allCommittedBackupsSwept = false;
                    }
                }
                else if (Directory.Exists(final))
                {
                    // The prior commit did not complete; the replacement may not be durable.
                    // Restore the last good dataset from the backup.
                    TryDeleteDirectory(final);
                    TryMoveDirectory(dir, final);
                }
                else
                {
                    // A crash left final absent but the prior data is in the backup: recover it.
                    TryMoveDirectory(dir, final);
                }
            }
        }

        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(file);
                var backupIndex = name.IndexOf(RootFileBackupInfix, StringComparison.Ordinal);
                if (backupIndex <= 0)
                {
                    continue;
                }

                var finalName = name[..backupIndex];
                var final = Path.Combine(root, finalName);
                if (committed && File.Exists(final))
                {
                    // Post-publication cleanup leftover; the new root file is kept.
                    TryDeleteFile(file);
                    if (File.Exists(file))
                    {
                        allCommittedBackupsSwept = false;
                    }
                }
                else if (File.Exists(final))
                {
                    TryDeleteFile(final);
                    TryMoveFile(file, final);
                }
                else
                {
                    TryMoveFile(file, final);
                }
            }
        }

        // Remove newly introduced paths (no prior backup, journaled) when the prior publication
        // did not complete, so the export converges to the prior complete dataset instead of
        // leaving a partial package (an old manifest plus a new conversation it does not describe).
        if (!committed)
        {
            foreach (var relative in ReadTransactionJournal(root))
            {
                var absolute = Path.Combine(root, relative);
                if (Directory.Exists(absolute))
                {
                    TryDeleteDirectory(absolute);
                }
                else if (File.Exists(absolute))
                {
                    TryDeleteFile(absolute);
                }
            }
        }

        // The transaction journal belongs to the previous export; remove it whether or not it
        // was used. The marker is removed once it has been used and every committed leftover has
        // been swept (kept while a committed leftover lingers so the next run retries the sweep).
        TryDeleteFile(TransactionJournalPath(root));
        if (!committed || allCommittedBackupsSwept)
        {
            TryDeleteFile(CommitMarkerPath(root));
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

    /// <summary>
    /// Restores one conversation backup on a failed global commit. If a prior existed (a backup
    /// is present), the not-yet-globally-committed new final is discarded and the backup renamed
    /// back into place; if the conversation was new (no backup), the new final is discarded to
    /// restore prior absence. Best-effort: a leftover is reconciled by
    /// <see cref="RecoverAndSweepStaging"/>.
    /// </summary>
    private static void RestoreConversationBackup(string? backup, string final)
    {
        if (backup is null)
        {
            TryDeleteDirectory(final);
            return;
        }

        if (!Directory.Exists(backup))
        {
            return;
        }

        TryDeleteDirectory(final);
        TryMoveDirectory(backup, final);
    }

    /// <summary>
    /// Restores one root-file backup on a failed global commit, mirroring
    /// <see cref="RestoreConversationBackup"/> for a single file.
    /// </summary>
    private static void RestoreRootFileBackup(string? backup, string final)
    {
        if (backup is null)
        {
            TryDeleteFile(final);
            return;
        }

        if (!File.Exists(backup))
        {
            return;
        }

        TryDeleteFile(final);
        TryMoveFile(backup, final);
    }

    private static void TryMoveFile(string source, string destination)
    {
        try
        {
            if (File.Exists(source) && !File.Exists(destination))
            {
                File.Move(source, destination);
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
