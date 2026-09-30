namespace WeArchive.Core.Abstractions;

/// <summary>
/// Publishes already-captured Raw Vault evidence for one conversation into the canonical archive.
/// docs/ARCHITECTURE.md section 3.6.1, docs/PRD.md FR-14.
/// <para>
/// The implementation reads preserved evidence only: it never contacts the live source and never
/// reacquires a database key. Each call advances that conversation's own ingest checkpoint inside
/// its own SQLite transaction, which is what lets a multi-conversation scope advance successful
/// conversations independently.
/// </para>
/// </summary>
public interface IConversationIngestService
{
    /// <summary>
    /// Ingests one conversation from verified Raw Vault generations for <paramref name="accountId"/>.
    /// </summary>
    /// <param name="accountId">The stable account id (<c>a_&lt;16 hex&gt;</c>) whose vault is read.</param>
    /// <param name="conversationSelector">
    /// The stable conversation id (<c>g_…</c>/<c>u_…</c>) or the upstream source conversation id.
    /// Both forms are accepted so a caller can consume the same identifier the discovery surface
    /// reports.
    /// </param>
    /// <param name="progress">Optional human progress; never machine-readable stdout.</param>
    /// <param name="cancellationToken">Cooperative cancellation; an in-flight conversation is rolled back.</param>
    /// <returns>The number of conversations published by this call (0 when nothing changed).</returns>
    /// <exception cref="Domain.ConversationNotInRawVaultException">
    /// The selector matched no conversation in any published generation for the account.
    /// </exception>
    Task<int> IngestConversationAsync(
        string accountId,
        string conversationSelector,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Ingests one conversation from a specific verified Raw Vault generation. Live sync uses this
    /// entry point for the generation it just captured; historical <c>ingest</c> continues to
    /// traverse generation history according to its own replay/checkpoint rules.
    /// </summary>
    /// <param name="accountId">The stable account id whose vault is read.</param>
    /// <param name="conversationSelector">Stable or source conversation id.</param>
    /// <param name="generationId">The exact newly published generation to consume.</param>
    /// <param name="progress">Optional human progress.</param>
    /// <param name="cancellationToken">Cooperative cancellation.</param>
    Task<int> IngestConversationFromGenerationAsync(
        string accountId,
        string conversationSelector,
        string generationId,
        IProgress<string>? progress,
        CancellationToken cancellationToken);
}
