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
/// </summary>
public sealed class JsonlDatasetExporter(IArchiveStore archive) : IDatasetExporter
{
    public const string ExportSchemaVersion = "1.0";
    public const string MessageSchemaVersion = "1.0";

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

            var absoluteFolder = Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(absoluteFolder))
            {
                // Guarantees no stale or duplicated partitions survive a re-export.
                Directory.Delete(absoluteFolder, recursive: true);
            }

            Directory.CreateDirectory(absoluteFolder);

            var written = WriteTimeline(absoluteFolder, relativeFolder, conversation.Id, messages, files, manifestFiles, cancellationToken);
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

        await WriteIdentitiesAsync(root, ledger, cancellationToken).ConfigureAwait(false);
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

        await WriteConversationsAsync(root, manifestConversations, cancellationToken).ConfigureAwait(false);
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

        WriteCollections(root);
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
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, ManifestOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "identities.yaml");
        var existing = File.Exists(path)
            ? YamlCatalogs.Deserialize<IdentityDocument>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
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

        await File.WriteAllTextAsync(
            path,
            YamlCatalogs.Serialize(document),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteConversationsAsync(
        string root,
        IReadOnlyList<ManifestConversation> conversations,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "conversations.yaml");
        var existing = File.Exists(path)
            ? YamlCatalogs.Deserialize<ConversationDocument>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
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

        await File.WriteAllTextAsync(
            path,
            YamlCatalogs.Serialize(document),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static void WriteCollections(string root)
    {
        var path = Path.Combine(root, "collections.yaml");
        if (File.Exists(path))
        {
            // collections.yaml is user-maintained; an export must never discard it.
            return;
        }

        File.WriteAllText(
            path,
            YamlCatalogs.Serialize(new CollectionDocument()),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
