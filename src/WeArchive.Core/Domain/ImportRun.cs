namespace WeArchive.Core.Domain;

public enum ImportRunStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// Audit record for one ingestion invocation. docs/DATA_MODEL.md section 13.
/// </summary>
public sealed record ImportRun
{
    public required string Id { get; init; }

    public required string AccountId { get; init; }

    public required string AdapterName { get; init; }

    public string? AdapterVersion { get; init; }

    public string? SourceVersion { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public required ImportRunStatus Status { get; init; }

    public int RecordsScanned { get; init; }

    public int RecordsInserted { get; init; }

    public int RecordsUpdated { get; init; }

    public int RecordsSkipped { get; init; }

    public int UnknownCount { get; init; }

    public int PartialCount { get; init; }

    public int WarningCount { get; init; }

    public int ErrorCount { get; init; }

    public IReadOnlyList<ImportDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>Counters returned by an idempotent message upsert.</summary>
public sealed record UpsertCounters
{
    public int Inserted { get; init; }

    public int Updated { get; init; }

    public int Unchanged { get; init; }

    public int Unknown { get; init; }

    public int Partial { get; init; }

    public int Total => Inserted + Updated + Unchanged;
}

/// <summary>Summary statistics for a conversation in the archive.</summary>
public sealed record ConversationStats
{
    public required string ConversationId { get; init; }

    public int MessageCount { get; init; }

    public int UnknownCount { get; init; }

    public int PartialCount { get; init; }

    public DateTimeOffset? FirstMessageAt { get; init; }

    public DateTimeOffset? LastMessageAt { get; init; }

    public IReadOnlyDictionary<CanonicalMessageType, int> TypeCounts { get; init; } =
        new Dictionary<CanonicalMessageType, int>();
}

/// <summary>Archive-level statistics.</summary>
public sealed record ArchiveStats
{
    public int AccountCount { get; init; }

    public int ConversationCount { get; init; }

    public int ParticipantCount { get; init; }

    public int MessageCount { get; init; }

    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>
    /// Instant of the newest archived message, or null for an empty archive. It is the canonical
    /// half of freshness reporting (docs/HARNESS.md section 10): the counts say how much is
    /// queryable, this says how current it is.
    /// </summary>
    public DateTimeOffset? LastMessageAt { get; init; }
}
