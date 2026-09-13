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
    /// Resolves reply targets for a conversation from archived records only.
    /// Used to upgrade quote snapshots into real relationships without inventing IDs.
    /// </summary>
    Task<int> ResolveReplyTargetsAsync(string conversationId, CancellationToken cancellationToken);

    Task<ConversationStats> GetConversationStatsAsync(
        string conversationId,
        CancellationToken cancellationToken);

    Task<ArchiveStats> GetArchiveStatsAsync(CancellationToken cancellationToken);
}

/// <summary>Injectable time source so that exports and runs stay testable and deterministic.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Local timezone offset used to render canonical timestamps.</summary>
    TimeSpan LocalOffset { get; }
}
