namespace WeArchive.Core.Domain;

/// <summary>
/// The requested canonical conversation is not present in the archive.
/// <para>
/// Export is a derivation from the current canonical state (docs/PRD.md G7, ADR 0008,
/// docs/EXPORT_PRD.md sections 3.2 and 15), so export cannot produce a dataset for a conversation
/// that no capture/ingest has published yet. The caller must refresh explicitly with
/// <c>wearchive sync --conversation &lt;id&gt;</c> before exporting; export never reads the live
/// source to create the conversation (Issue #66).
/// </para>
/// </summary>
public sealed class ConversationNotArchivedException(string conversationId)
    : InvalidOperationException(
        $"Conversation '{conversationId}' is not present in the canonical archive. " +
        $"Run 'wearchive sync --conversation {conversationId}' before exporting.")
{
    /// <summary>The canonical conversation id (or selector) that the archive does not hold.</summary>
    public string ConversationId { get; } = conversationId;
}