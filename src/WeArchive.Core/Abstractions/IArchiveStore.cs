using WeArchive.Core.Domain;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// The archive is the system of record for normalized data.
/// docs/ARCHITECTURE.md section 3.6. All writes are idempotent upserts keyed by
/// stable IDs so that re-running an import never creates logical duplicates.
/// </summary>
public interface IArchiveStore
{
    string ArchivePath { get; }

    /// <summary>Applies pending schema migrations.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<ImportRun> BeginImportRunAsync(
        string accountId,
        SourceDescriptor descriptor,
        CancellationToken cancellationToken);

    Task CompleteImportRunAsync(ImportRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the single archive transaction for one conversation import.
    /// docs/ARCHITECTURE.md section 3.2.1.
    /// <para>
    /// The conversation row is written inside that transaction and stays invisible to the rest
    /// of the archive until <see cref="IConversationImportSession.CommitAsync"/> publishes it.
    /// Publishing is what keeps the archive — the system of record — from recording a partial
    /// conversation when the source could not be read completely (docs/PRD.md FR-14).
    /// </para>
    /// </summary>
    Task<IConversationImportSession> BeginConversationImportAsync(
        ArchiveConversation conversation,
        CancellationToken cancellationToken);

    Task UpsertAccountAsync(ArchiveAccount account, CancellationToken cancellationToken);

    Task UpsertParticipantsAsync(
        IEnumerable<ArchiveParticipant> participants,
        CancellationToken cancellationToken);

    Task UpsertConversationsAsync(
        IEnumerable<ArchiveConversation> conversations,
        CancellationToken cancellationToken);

    /// <summary>Idempotent message upsert. Returns inserted/updated/unchanged counters.</summary>
    Task<UpsertCounters> UpsertMessagesAsync(
        IReadOnlyList<CanonicalMessage> messages,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveAccount>> ListAccountsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveConversation>> ListConversationsAsync(
        string accountId,
        CancellationToken cancellationToken);

    Task<ArchiveConversation?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ArchiveParticipant>> ListParticipantsAsync(
        string accountId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CanonicalMessage>> ReadMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one bounded page of a conversation's canonical timeline.
    /// docs/DATA_MODEL.md section 23, docs/HARNESS.md section 5.
    /// <para>
    /// Ordering is the canonical timeline order <c>(occurred_utc, source_order_key, id)</c>, and
    /// <see cref="ArchiveMessageQuery.After"/> is an exclusive keyset position, so consecutive
    /// pages neither repeat nor omit a record with an equal timestamp.
    /// </para>
    /// <para>
    /// The implementation returns at most <see cref="ArchiveMessageQuery.Limit"/> items and reports
    /// whether the archive held further matching records.
    /// </para>
    /// </summary>
    Task<ArchiveMessagePage> QueryMessagesAsync(
        ArchiveMessageQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the bounded canonical window around one stable message ID, or null when the archive
    /// holds no such message. The window is taken in canonical timeline order within the target's
    /// own conversation.
    /// </summary>
    Task<ArchiveMessageContext?> ReadMessageContextAsync(
        string messageId,
        int before,
        int after,
        CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates every persisted ingest checkpoint row.
    /// <para>
    /// This is a neutral read of rows the archive owns: it does not interpret the opaque
    /// <see cref="IngestCheckpoint.CheckpointJson"/> payload and does not classify the scope
    /// vocabulary, both of which belong to the component that writes the cursor. A caller that
    /// needs a summary of ingest progress projects it through
    /// <see cref="IIngestProgressSource"/>.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<IngestCheckpoint>> ListIngestCheckpointsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Resolves reply targets for a conversation from archived records only.
    /// Used to upgrade quote snapshots into real relationships without inventing IDs.
    /// </summary>
    Task<int> ResolveReplyTargetsAsync(string conversationId, CancellationToken cancellationToken);

    Task<ConversationStats> GetConversationStatsAsync(
        string conversationId,
        CancellationToken cancellationToken);

    Task<ArchiveStats> GetArchiveStatsAsync(CancellationToken cancellationToken);

    Task<IngestCheckpoint?> GetIngestCheckpointAsync(
        string accountId, string adapterFamily, string scopeKind, string scopeId, CancellationToken cancellationToken);

    /// <summary>Persists an account-level scan cursor after every conversation in a generation was examined.</summary>
    Task SetIngestCheckpointAsync(IngestCheckpoint checkpoint, CancellationToken cancellationToken);
}

/// <summary>Injectable time source so that exports and runs stay testable and deterministic.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Local timezone offset used to render canonical timestamps.</summary>
    TimeSpan LocalOffset { get; }
}

/// <summary>
/// The staged, all-or-nothing write transaction of one conversation import.
/// docs/ARCHITECTURE.md sections 3.2.1 and 11.
/// <para>
/// Everything written through a session belongs to the import that opened it and becomes part
/// of the archive only when that import commits. A run that could not read the source
/// completely rolls the session back instead, so the archive keeps the exact state it had
/// before the run: no partial conversation, no partial message batches, and no aggregates that
/// describe data the archive does not hold.
/// </para>
/// <para>
/// A session owns archive resources until it is disposed; it must be committed or rolled back
/// before the import-run audit row is written, because both write to the same database file.
/// </para>
/// </summary>
public interface IConversationImportSession : IAsyncDisposable
{
    /// <summary>Idempotent message upsert inside the staged transaction.</summary>
    Task<UpsertCounters> UpsertMessagesAsync(
        IReadOnlyList<CanonicalMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>Stages ingest progress in the same transaction as conversation publication.</summary>
    Task SetIngestCheckpointAsync(IngestCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the staged conversation — recomputing its first/last/message-count aggregates
    /// from the records this transaction wrote — and commits.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Discards every write staged by this session.</summary>
    Task RollbackAsync();
}
