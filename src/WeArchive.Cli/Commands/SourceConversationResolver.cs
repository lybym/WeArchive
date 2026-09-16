using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// Resolves a <c>--conversation &lt;id-or-alias&gt;</c> selector to a source account and
/// source conversation, for commands that read from the local source.
/// <para>
/// This is presentation-layer selection only. It reuses the source catalog and the stable
/// upstream conversation id the adapter already reports; it introduces no second alias
/// catalogue (docs/EXPORT_PRD.md section 7 notes alias/collection selection as a future
/// product requirement layered on the same engine). It contains no normalization,
/// id-derivation or archive publication logic.
/// </para>
/// </summary>
internal sealed class SourceConversationResolver(SourceCatalogService catalog)
{
    private readonly SourceCatalogService _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>
    /// Resolves the selector. The current logged-in account is preferred; otherwise the first
    /// account is used. The CLI never prompts for this choice, so <c>--no-input</c> stays
    /// automation-safe. A selector that matches no source conversation is an operation
    /// failure (exit 1), not a usage syntax error.
    /// </summary>
    public async Task<(SourceAccount Account, SourceConversation Conversation)> ResolveAsync(
        string conversation,
        CancellationToken cancellationToken)
    {
        var accounts = await _catalog.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (accounts.Count == 0)
        {
            throw new InvalidOperationException("No source account was found.");
        }

        var account = accounts.FirstOrDefault(a => a.IsCurrent) ?? accounts[0];
        var conversations = await _catalog
            .ListConversationsAsync(account.SourceProfileId, cancellationToken)
            .ConfigureAwait(false);

        var match = conversations
            .FirstOrDefault(c => string.Equals(c.SourceConversationId, conversation, StringComparison.Ordinal));

        if (match is null)
        {
            throw new InvalidOperationException(
                $"conversation '{conversation}' was not found in the source.");
        }

        return (account, match);
    }
}
