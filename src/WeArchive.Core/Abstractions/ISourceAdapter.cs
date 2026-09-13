using WeArchive.Core.Domain;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// The only way the application reaches a local source.
/// docs/ARCHITECTURE.md section 3.3 and section 6.
/// <para>
/// Implementations are read-only towards the source. An adapter must never write
/// exports, bypass the archive model, hide unsupported records or fabricate
/// identities, URLs, amounts or content.
/// </para>
/// </summary>
public interface ISourceAdapter
{
    string AdapterName { get; }

    string AdapterVersion { get; }

    /// <summary>Reports source availability, upstream version and probe diagnostics.</summary>
    Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken);

    /// <summary>Enumerates locally available source profiles (logged-in accounts).</summary>
    Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken);

    /// <summary>Enumerates conversations for a source profile, with display metadata where available.</summary>
    Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams normalized-ready records for one conversation in ascending time order.
    /// Records the parser cannot interpret must still be emitted as
    /// <see cref="SourceContentKind.Unknown"/>, never dropped.
    /// </summary>
    IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken);

    /// <summary>Enumerates participant identity metadata for a source profile.</summary>
    Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cheaply describes one conversation (record count, time range, participants)
    /// without reading the message bodies, so the UI can preview a selection.
    /// </summary>
    Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken);
}
