using WeArchive.Core.Domain;

namespace WeArchive.Cli.Commands;

/// <summary>
/// The one <c>--account &lt;id&gt;</c> selector contract every CLI command must share
/// (docs/CLI.md, Issue #47).
/// <para>
/// A selector resolves a source account when it is exactly (case-sensitively,
/// <see cref="StringComparison.Ordinal"/>) equal to the account's source profile id or to its
/// canonical stable account id, <c>StableIds.Account(adapterName, sourceProfileId)</c>. Both
/// identifiers are opaque source-derived strings whose own identity rules are case-sensitive: the
/// stable id is a digest of the exact profile id, so a case-insensitive profile-id match would let
/// <c>capture</c> select a profile whose derived stable id differs from the one the caller named,
/// and two profiles differing only in casing would resolve ambiguously. Selectors are therefore
/// matched verbatim, exactly as <c>account list</c> prints them; an unmatched selector fails closed
/// as <c>account_not_found</c> and never falls back to the current account.
/// </para>
/// <para>
/// The selector is resolved from the enumerated account list, so matching stays source-neutral: the
/// adapter boundary decides which profiles exist, the CLI only compares identifiers. Keeping the
/// comparison here is what stops the per-command copies from drifting apart again.
/// </para>
/// </summary>
internal static class AccountSelector
{
    public static bool Matches(string adapterName, SourceAccount account, string selector)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(selector);

        return string.Equals(account.SourceProfileId, selector, StringComparison.Ordinal) ||
            string.Equals(StableIds.Account(adapterName, account.SourceProfileId), selector, StringComparison.Ordinal);
    }
}
