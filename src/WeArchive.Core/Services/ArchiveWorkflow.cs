using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;

namespace WeArchive.Core.Services;

public sealed record ExportConversationRequest
{
    public required string SourceProfileId { get; init; }

    public required string SourceConversationId { get; init; }

    public required ConversationKind Kind { get; init; }

    public string? PeerSourceUserId { get; init; }

    public string? ConversationTitle { get; init; }

    public required string OutputDirectory { get; init; }
}

/// <summary>
/// The single user-facing operation of this MVP:
/// source -&gt; adapter -&gt; normalizer -&gt; SQLite archive -&gt; JSONL dataset.
/// <para>
/// Import and export are deliberately one workflow here so that the archive stays
/// the source of truth for the export. The exporter never reads upstream files.
/// </para>
/// </summary>
public sealed class ArchiveWorkflow(
    SourceCatalogService catalog,
    ImportService importer,
    IDatasetExporter exporter,
    IArchiveStore archive,
    IClock clock)
{
    private readonly SourceCatalogService _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly ImportService _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    private readonly IDatasetExporter _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    public async Task<ExportResult> ExportConversationAsync(
        ExportConversationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var diagnostics = new List<ImportDiagnostic>();
        var failures = new DiagnosticBag();

        progress?.Report(OperationProgress.Of(OperationStages.ProbingSource, detail: request.SourceConversationId));

        SourceConversationDetail? detail = null;
        try
        {
            detail = await _catalog
                .DescribeConversationAsync(request.SourceProfileId, request.SourceConversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed preview must not abort the export; the real read reports properly.
            failures.Partial(
                DiagnosticCodes.PartitionUnreadable,
                "Could not pre-read conversation metadata; progress totals may be approximate.");
        }

        var outcome = await _importer.ImportConversationAsync(
            new ImportRequest
            {
                SourceProfileId = request.SourceProfileId,
                SourceConversationId = request.SourceConversationId,
                Kind = request.Kind,
                PeerSourceUserId = request.PeerSourceUserId,
                ConversationTitle = request.ConversationTitle,
                TotalHint = detail?.MessageCount,
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        // A non-completed import must not fall through to an empty dataset export. A
        // missing or unreadable message shard, for example, surfaces a Fatal diagnostic
        // and a Failed run here (FR-14).
        if (outcome.Run.Status != ImportRunStatus.Completed)
        {
            var fatal = outcome.Diagnostics.LastOrDefault(d => d.Severity == DiagnosticSeverity.Fatal);
            throw new InvalidOperationException(
                fatal?.Message ?? $"Import did not complete (status: {outcome.Run.Status}).");
        }

        diagnostics.AddRange(outcome.Diagnostics);
        diagnostics.AddRange(failures.Items);

        progress?.Report(OperationProgress.Of(
            OperationStages.Exporting,
            outcome.Counters.Total,
            outcome.Counters.Total,
            outcome.ConversationId));

        var result = await _exporter.ExportAsync(
            new ExportRequest
            {
                OutputDirectory = request.OutputDirectory,
                AccountId = StableIds.Account(_catalog.AdapterName, request.SourceProfileId),
                ConversationIds = [outcome.ConversationId],
                CreatedAt = _clock.UtcNow,
                // Completeness diagnostics travel with the dataset so a consumer can judge it
                // from manifest.json alone.
                Diagnostics = diagnostics,
            },
            progress is null ? null : new Progress<ExportProgress>(p =>
                progress.Report(OperationProgress.Of(p.Stage, p.Processed, p.Total, p.ConversationId))),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(OperationProgress.Of(
            OperationStages.Completed,
            result.RecordCount,
            result.RecordCount,
            outcome.ConversationId));

        return result with { Diagnostics = [.. result.Diagnostics, .. diagnostics] };
    }

    /// <summary>Cheap health snapshot for the environment panel.</summary>
    public async Task<ArchiveStats> GetArchiveStatsAsync(CancellationToken cancellationToken)
    {
        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _archive.GetArchiveStatsAsync(cancellationToken).ConfigureAwait(false);
    }
}
