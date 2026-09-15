using WeArchive.Core.Export;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// Produces the Phase 1 machine-oriented dataset from the archive.
/// Implementations read normalized archive models only; they must never touch
/// upstream source files. docs/ARCHITECTURE.md section 3.7.
/// </summary>
public interface IDatasetExporter
{
    /// <summary>Version recorded in <c>manifest.json</c>.</summary>
    string ExporterVersion { get; }

    Task<ExportResult> ExportAsync(
        ExportRequest request,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>Stages reported while an import+export operation runs.</summary>
public static class OperationStages
{
    public const string ProbingSource = "probing_source";
    public const string ListingConversations = "listing_conversations";
    public const string ReadingMessages = "reading_messages";
    public const string Archiving = "archiving";
    public const string Exporting = "exporting";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

/// <summary>Progress payload shared by the import and export stages.</summary>
public sealed record OperationProgress
{
    public required string Stage { get; init; }

    public int Processed { get; init; }

    public int Total { get; init; }

    public string? Detail { get; init; }

    public static OperationProgress Of(string stage, int processed = 0, int total = 0, string? detail = null) =>
        new() { Stage = stage, Processed = processed, Total = total, Detail = detail };
}
