namespace WeArchive.Core.Domain;

/// <summary>
/// Engineering provenance for a normalized record.
/// docs/DATA_MODEL.md section 15 and docs/MESSAGE_SCHEMA.md section 3.4.
/// These fields exist for diagnostics and reprocessing; they are not the
/// downstream LLM interface.
/// </summary>
public sealed record SourceProvenance
{
    public required string SourceProfileId { get; init; }

    public required string SourceConversationId { get; init; }

    /// <summary>Stable upstream record identifier when one exists.</summary>
    public string? SourceMessageId { get; init; }

    /// <summary>Upstream message type code, preserved verbatim for later parser work.</summary>
    public string? SourceType { get; init; }

    /// <summary>Upstream message subtype code, preserved verbatim.</summary>
    public string? SourceSubtype { get; init; }

    /// <summary>
    /// Upstream partition/shard the record came from, e.g. <c>message_0</c>.
    /// Needed because one logical timeline is merged from several upstream files.
    /// </summary>
    public string? SourcePartition { get; init; }

    /// <summary>Upstream ordering key within the partition (local auto-increment id).</summary>
    public string? SourceOrderKey { get; init; }

    public required string AdapterName { get; init; }

    public string? AdapterVersion { get; init; }

    public string? SourceVersion { get; init; }

    public string? ImportRunId { get; init; }
}
