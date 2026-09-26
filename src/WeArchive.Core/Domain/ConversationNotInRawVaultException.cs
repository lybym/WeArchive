namespace WeArchive.Core.Domain;

/// <summary>
/// A typed signal that a requested conversation could not be resolved in the preserved Raw Vault
/// evidence for the requested account, so no canonical publication was attempted.
/// <para>
/// It is a distinct type rather than a generic operation failure because a multi-scope caller
/// (<c>sync --collection</c>) must report an unresolved Collection member differently from a
/// capture/ingest failure that was actually attempted and rolled back
/// (docs/PRD.md FR-23, docs/ARCHITECTURE.md section 3.8).
/// </para>
/// </summary>
public sealed class ConversationNotInRawVaultException : Exception
{
    public ConversationNotInRawVaultException(string conversationSelector, string accountId)
        : base($"Conversation '{conversationSelector}' was not found in Raw Vault account '{accountId}'.")
    {
        ConversationSelector = conversationSelector;
        AccountId = accountId;
    }

    /// <summary>The requested conversation selector (stable conversation id or upstream id).</summary>
    public string ConversationSelector { get; }

    /// <summary>The stable Raw Vault account id that was searched.</summary>
    public string AccountId { get; }
}