using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Services;

/// <summary>The outcome of resolving a conversation selector against the canonical archive.</summary>
public enum ArchiveConversationResolutionStatus
{
    /// <summary>The selector identified exactly one archived conversation.</summary>
    Resolved,

    /// <summary>The archive holds no conversation for the selector.</summary>
    NotFound,

    /// <summary>
    /// An upstream source conversation id matches more than one archived conversation (for example
    /// the same upstream id under two archived accounts). The caller must use the canonical stable
    /// id; no account is chosen arbitrarily.
    /// </summary>
    Ambiguous,
}

/// <summary>
/// One archive-only resolution result. <see cref="Candidates"/> carries the deterministically
/// ordered matches for the ambiguous case so the caller can report them without re-reading the
/// archive.
/// </summary>
public sealed record ArchiveConversationResolution
{
    public required ArchiveConversationResolutionStatus Status { get; init; }

    public ArchiveConversation? Conversation { get; init; }

    public IReadOnlyList<ArchiveConversation> Candidates { get; init; } = [];
}

/// <summary>
/// Resolves <c>--conversation &lt;id-or-alias&gt;</c> entirely from the canonical archive.
/// <para>
/// The canonical stable conversation id (<c>g_…</c>/<c>u_…</c>) is the primary, unambiguous
/// selector and resolves with a direct archive lookup. An upstream source conversation id remains
/// supported for compatibility only when it resolves uniquely from archived conversations; when
/// the same upstream id exists under more than one archived account the result is
/// <see cref="ArchiveConversationResolutionStatus.Ambiguous"/> rather than an arbitrary pick.
/// </para>
/// <para>
/// This resolver touches no <c>ISourceAdapter</c>, source catalog, WeChat key or live discovery:
/// export must be a read-only derivation from the canonical archive (Issue #66, Gap A/Gap E,
/// docs/PRD.md G7/G10, ADR 0008). Alias/Collection selection is deliberately not implemented here.
/// </para>
/// </summary>
public sealed class ArchiveConversationResolver(IArchiveStore archive)
{
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));

    public async Task<ArchiveConversationResolution> ResolveAsync(
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);

        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(selector))
        {
            return new ArchiveConversationResolution { Status = ArchiveConversationResolutionStatus.NotFound };
        }

        var direct = await _archive.GetConversationAsync(selector, cancellationToken).ConfigureAwait(false);
        if (direct is not null)
        {
            return Resolved(direct);
        }

        // Accounts and conversations are visited in a stable order so both the outcome and the
        // reported candidate list are deterministic for a given archive state.
        var matches = new List<ArchiveConversation>();
        var accounts = await _archive.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            var conversations = await _archive
                .ListConversationsAsync(account.Id, cancellationToken)
                .ConfigureAwait(false);

            foreach (var conversation in conversations)
            {
                if (string.Equals(conversation.SourceConversationId, selector, StringComparison.Ordinal))
                {
                    matches.Add(conversation);
                }
            }
        }

        if (matches.Count == 0)
        {
            return new ArchiveConversationResolution { Status = ArchiveConversationResolutionStatus.NotFound };
        }

        matches.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));

        return matches.Count == 1
            ? Resolved(matches[0])
            : new ArchiveConversationResolution
            {
                Status = ArchiveConversationResolutionStatus.Ambiguous,
                Candidates = matches,
            };
    }

    private static ArchiveConversationResolution Resolved(ArchiveConversation conversation) => new()
    {
        Status = ArchiveConversationResolutionStatus.Resolved,
        Conversation = conversation,
        Candidates = [conversation],
    };
}