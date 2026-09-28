using WeArchive.Core.RawVault;
using WeArchive.Core.Services;

namespace WeArchive.Core.Collections;

/// <summary>Request for <c>CollectionSyncService.SyncAsync</c>.</summary>
public sealed record CollectionSyncRequest
{
    /// <summary>The Collection name to synchronize.</summary>
    public required string CollectionName { get; init; }

    /// <summary>
    /// The source profile to capture. When null the current account is auto-selected, which never
    /// prompts and is therefore safe under <c>--no-input</c>.
    /// </summary>
    public string? SourceProfileId { get; init; }
}

/// <summary>
/// Per-conversation outcome of a Collection sync. Collection execution is multi-scope, not one
/// transaction, so every requested conversation reports its own state.
/// </summary>
public enum CollectionSyncItemStatus
{
    /// <summary>The conversation's evidence changed and its canonical publication committed.</summary>
    Succeeded,

    /// <summary>The conversation was verified and nothing changed; its checkpoint kept its value.</summary>
    NoChange,

    /// <summary>A capture/ingest failure rolled this conversation back; other conversations are unaffected.</summary>
    Failed,

    /// <summary>The requested member resolved to no conversation in the captured evidence.</summary>
    Unresolved,
}

/// <summary>One requested Collection member and what happened to it.</summary>
public sealed record CollectionSyncItem
{
    /// <summary>The stable conversation id the member requested.</summary>
    public required string ConversationId { get; init; }

    public required CollectionSyncItemStatus Status { get; init; }

    /// <summary>Conversations published by this member's ingest call (0 or 1).</summary>
    public int ConversationsIngested { get; init; }

    /// <summary>The engineering reason this member did not succeed; null on success/no-change.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// The structured result of one Collection sync. <see cref="Succeeded"/> is true only when every
/// requested member succeeded or was verified unchanged, so a partially successful run is never
/// described as a total success (docs/PRD.md FR-23, docs/ARCHITECTURE.md section 3.8).
/// </summary>
public sealed record CollectionSyncResult
{
    public required string CollectionName { get; init; }

    /// <summary>True only when no requested member failed or was left unresolved.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Per-member outcomes in Collection declaration order.</summary>
    public required IReadOnlyList<CollectionSyncItem> Items { get; init; }

    /// <summary>Stable account id the evidence was captured from; null when no capture ran.</summary>
    public string? AccountId { get; init; }

    /// <summary>Source profile id the evidence was captured from; null when no capture ran.</summary>
    public string? SourceProfileId { get; init; }

    /// <summary>The Raw Vault generation the members were ingested from; null when no capture ran.</summary>
    public string? GenerationId { get; init; }

    /// <summary>Whether the capture reused verified evidence or read the whole source.</summary>
    public RawCaptureMode? CaptureMode { get; init; }

    /// <summary>Members the configuration declared that are not stable conversation ids.</summary>
    public IReadOnlyList<string> InvalidConversationIds { get; init; } = [];

    /// <summary>Members the configuration declared more than once.</summary>
    public IReadOnlyList<string> DuplicateConversationIds { get; init; } = [];

    public int SucceededCount => Items.Count(i => i.Status == CollectionSyncItemStatus.Succeeded);

    public int NoChangeCount => Items.Count(i => i.Status == CollectionSyncItemStatus.NoChange);

    public int FailedCount => Items.Count(i => i.Status is CollectionSyncItemStatus.Failed or CollectionSyncItemStatus.Unresolved);
}

/// <summary>
/// Capturing live-source evidence for a Collection did not publish a usable generation, so no
/// member could be ingested. Capture is account-scoped and precedes per-member work, so this is an
/// operation-level failure rather than a fabricated per-member failure.
/// <para>
/// It derives from the shared <see cref="SyncCaptureException"/> so every preservation-first sync
/// scope surfaces the same capture condition, while the Collection keeps its scope detail.
/// </para>
/// </summary>
public sealed class CollectionCaptureException : SyncCaptureException
{
    public CollectionCaptureException(string collectionName, string message)
        : base($"Capturing evidence for collection '{collectionName}' failed: {message}", message)
    {
        CollectionName = collectionName;
    }

    public string CollectionName { get; }
}
