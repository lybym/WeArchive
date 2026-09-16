using WeArchive.Cli.CommandLine;
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
/// <para>
/// Resolution mirrors <c>conversation show</c>: the selector is matched by the canonical
/// stable archive id (<c>g_…</c>/<c>u_…</c>) <em>or</em> the upstream source conversation id,
/// so a caller may use the same identifier the discovery surface reports before and after
/// import (docs/DATA_MODEL.md section 16). Each failure mode maps to the documented granular
/// <c>error.code</c> value rather than a generic failure, keeping the machine contract
/// consistent with the discovery family.
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
    /// <para>
    /// On failure a structured error document is written to <paramref name="context"/> (using
    /// the granular codes also used by <c>conversation list</c>/<c>show</c>) and <c>null</c> is
    /// returned; the caller returns <see cref="ExitCode.Failure"/>. On success the resolved
    /// account and conversation are returned.
    /// </para>
    /// </summary>
    public async Task<(SourceAccount Account, SourceConversation Conversation)?> ResolveAsync(
        CliContext context,
        string conversation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SourceAccount> accounts;
        try
        {
            accounts = await _catalog.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.SourceUnavailable, ex.Message);
            return null;
        }

        if (accounts.Count == 0)
        {
            context.WriteError(
                CliErrorCode.NoAccounts,
                "no source profiles are available; cannot resolve a conversation profile.");
            return null;
        }

        var account = accounts.FirstOrDefault(a => a.IsCurrent) ?? accounts[0];
        var accountId = StableIds.Account(_catalog.AdapterName, account.SourceProfileId);

        IReadOnlyList<SourceConversation> conversations;
        try
        {
            conversations = await _catalog
                .ListConversationsAsync(account.SourceProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.ConversationListFailed, ex.Message);
            return null;
        }

        // Match by the canonical stable archive id (the same derivation the importer and the
        // discovery surface use) or by the upstream source conversation id, exactly as
        // `conversation show` resolves — so `--conversation g_<16-hex>` resolves the same
        // conversation `conversation show` and `conversation list` report (docs/DATA_MODEL.md
        // section 16). No second alias store is introduced.
        SourceConversation? match = null;
        foreach (var candidate in conversations)
        {
            var stableId = StableIds.Conversation(
                accountId, candidate.Kind, candidate.SourceConversationId, candidate.PeerSourceUserId);

            if (string.Equals(stableId, conversation, StringComparison.Ordinal) ||
                string.Equals(candidate.SourceConversationId, conversation, StringComparison.Ordinal))
            {
                match = candidate;
                break;
            }
        }

        if (match is null)
        {
            context.WriteError(
                CliErrorCode.ConversationNotFound,
                $"conversation '{conversation}' was not found.");
            return null;
        }

        return (account, match);
    }
}
