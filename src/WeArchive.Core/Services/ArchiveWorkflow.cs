using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;

namespace WeArchive.Core.Services;

/// <summary>
/// One canonical-archive-only export request. The operation needs canonical identifiers plus the
/// output configuration only: no live-source descriptor, upstream selector, account profile or
/// conversation title is accepted here (Issue #66).
/// </summary>
public sealed record ExportConversationRequest
{
    /// <summary>Stable canonical conversation ID (<c>g_…</c>/<c>u_…</c>).</summary>
    public required string ConversationId { get; init; }

    /// <summary>Absolute directory that will receive the export package.</summary>
    public required string OutputDirectory { get; init; }
}

/// <summary>
/// The canonical export workflow: <c>Canonical SQLite -&gt; JSONL/YAML/JSON</c>.
/// <para>
/// The canonical archive is the operational system of record; the dataset is derived/interchange
/// state (docs/PRD.md G7/G10, docs/adr/0008-raw-vault-canonical-query-layers.md,
/// docs/EXPORT_PRD.md sections 3.2 and 15). This workflow therefore depends on the exporter, the
/// archive and the clock only: it must never reach a source catalog, <see cref="ImportService"/>,
/// <c>ISourceAdapter</c>, live conversation discovery/probing, WeChat key acquisition, capture or
/// Raw Vault ingest.
/// </para>
/// <para>
/// A caller that wants fresher canonical state refreshes explicitly with <c>wearchive sync</c>
/// first; export never re-imports, never upserts and never mutates canonical state, checkpoints or
/// Raw Vault artifacts. Manifest diagnostics are projected from persisted archive/audit state
/// instead (Issue #66, Gaps B/C/D).
/// </para>
/// <para>
/// R1 publication behavior (staging plus in-process backup/restore, with no persisted recovery
/// state) is owned by <see cref="IDatasetExporter"/> and is unchanged by this boundary.
/// </para>
/// </summary>
public sealed class ArchiveWorkflow(
    IDatasetExporter exporter,
    IArchiveStore archive,
    IClock clock)
{
    private readonly IDatasetExporter _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Derives one conversation's dataset from the current canonical archive state.
    /// <para>
    /// The conversation must already be archived; otherwise
    /// <see cref="ConversationNotArchivedException"/> is thrown so the caller can report the
    /// deterministic conversation-not-found failure instead of silently reading the live source.
    /// </para>
    /// </summary>
    public async Task<ExportResult> ExportConversationAsync(
        ExportConversationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var conversation = await _archive
            .GetConversationAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConversationNotArchivedException(request.ConversationId);

        // Manifest diagnostics come from persisted audit state only. The archive already owns the
        // import-run diagnostics for the runs that wrote this conversation's records, so nothing
        // here may consult the live source to "refresh" them (Issue #66, Gap D).
        var diagnostics = await _archive
            .ReadConversationDiagnosticsAsync(conversation.Id, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(OperationProgress.Of(
            OperationStages.Exporting,
            conversation.MessageCount,
            conversation.MessageCount,
            conversation.Id));

        var result = await _exporter.ExportAsync(
            new ExportRequest
            {
                OutputDirectory = request.OutputDirectory,
                // Account/source metadata is read from archived account state by the exporter.
                AccountId = conversation.AccountId,
                ConversationIds = [conversation.Id],
                CreatedAt = _clock.UtcNow,
                // Completeness diagnostics travel with the dataset so a consumer can judge it from
                // manifest.json alone.
                Diagnostics = diagnostics,
            },
            progress is null ? null : new Progress<ExportProgress>(p =>
                progress.Report(OperationProgress.Of(p.Stage, p.Processed, p.Total, p.ConversationId))),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(OperationProgress.Of(
            OperationStages.Completed,
            result.RecordCount,
            result.RecordCount,
            conversation.Id));

        return result with { Diagnostics = [.. result.Diagnostics, .. diagnostics] };
    }
}