namespace WeArchive.Core.Domain;

/// <summary>
/// Source-neutral intermediate produced by a source adapter's parsers.
/// <para>
/// This is the adapter/normalizer boundary. Upstream wire formats (WeChat XML,
/// numeric type codes, database columns) must be translated into this shape by
/// the adapter; they must never reach the normalizer or the archive.
/// </para>
/// </summary>
public enum SourceContentKind
{
    Text,
    Image,
    Voice,
    Video,
    File,
    Link,
    AppShare,
    MiniProgram,
    ForwardBundle,
    Location,
    ContactCard,
    System,
    Revoke,
    RedPacket,
    Transfer,
    Emoji,
    Unknown,
}

/// <summary>One nested item inside a merged/forwarded chat bundle.</summary>
public sealed record SourceForwardItem
{
    public string? SenderName { get; init; }

    /// <summary>Only set when identity resolution is reliable; never guessed.</summary>
    public string? SenderId { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public required CanonicalMessageType Type { get; init; }

    public required string Text { get; init; }
}

/// <summary>A locally available quote snapshot attached to a message.</summary>
public sealed record SourceReplySnapshot
{
    /// <summary>Upstream message ID of the referenced message, when available.</summary>
    public string? SourceMessageId { get; init; }

    /// <summary>Upstream sender id of the referenced message.</summary>
    public string? SenderSourceId { get; init; }

    /// <summary>Display name as presented by the quote snapshot.</summary>
    public string? SenderName { get; init; }

    public string? Text { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }
}

/// <summary>
/// Type-specific semantics extracted from a single upstream record.
/// A null/absent member means "not available in the local source"; it never means
/// "guess something". docs/MESSAGE_SCHEMA.md section 20.
/// </summary>
public sealed record SourceMessageContent
{
    public required SourceContentKind Kind { get; init; }

    /// <summary>Plain semantic text for <see cref="SourceContentKind.Text"/>.</summary>
    public string? Text { get; init; }

    // --- voice ---
    public int? DurationSeconds { get; init; }

    // --- file ---
    public string? FileName { get; init; }
    public string? FileExtension { get; init; }
    public long? FileSizeBytes { get; init; }

    // --- link / app_share / mini_program ---
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? OriginalUrl { get; init; }
    public string? FallbackUrl { get; init; }
    public string? SourceApp { get; init; }
    public string? AppId { get; init; }
    public string? PagePath { get; init; }

    // --- location ---
    public string? Label { get; init; }
    public string? Address { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    // --- contact card ---
    public string? CardDisplayName { get; init; }
    public string? CardSourceUserId { get; init; }

    // --- transfer / red packet ---
    public string? Amount { get; init; }
    public string? Currency { get; init; }
    public string? TransferStatus { get; init; }

    // --- forward bundle ---
    public int? ForwardItemCount { get; init; }
    public IReadOnlyList<SourceForwardItem>? ForwardItems { get; init; }

    // --- system ---
    public string? SystemText { get; init; }
    public string? SystemEvent { get; init; }
    public IReadOnlyList<string>? ActorSourceIds { get; init; }
    public IReadOnlyList<string>? TargetSourceIds { get; init; }

    // --- revoke ---
    public string? OperatorSourceId { get; init; }
    public string? RevokedSourceMessageId { get; init; }
    public string? RevokedText { get; init; }

    // --- unknown / partial diagnostics ---
    public string? RawSummary { get; init; }

    /// <summary>True when only part of the upstream record could be interpreted.</summary>
    public bool IsPartial { get; init; }

    /// <summary>A plain text message.</summary>
    public static SourceMessageContent PlainText(string text) =>
        new() { Kind = SourceContentKind.Text, Text = text };

    /// <summary>An unparsed record. Upstream codes are preserved for later parser work.</summary>
    public static SourceMessageContent Unparsed(string? rawSummary) =>
        new() { Kind = SourceContentKind.Unknown, RawSummary = rawSummary, IsPartial = true };
}
