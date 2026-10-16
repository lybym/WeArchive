using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Export;

namespace WeArchive.Core.Services;

/// <summary>
/// One canonical-archive-only export request. The operation needs canonical identifiers plus the
/// output configuration only: no live-source descriptor, upstream selector, account profile or
/// conversation title is accepted here (Issue #66).
/// <para>
/// The optional <c>Since</c>/<c>Until</c> bounds refine the export to a selected time range
/// (docs/PRD.md FR-18, docs/EXPORT_PRD.md section 7). Both are inclusive instants compared at the
/// archive's one-second resolution, exactly as the canonical query surfaces interpret their
/// bounds; the CLI accepts the documented <c>message list</c> time grammar and maps it onto these
/// instants.
/// </para>
/// </summary>
public sealed record ExportConversationRequest
{
    /// <summary>Stable canonical conversation ID (<c>g_…</c>/<c>u_…</c>).</summary>
    public required string ConversationId { get; init; }

    /// <summary>Absolute directory that will receive the export package.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Optional inclusive lower time bound; null keeps the side unbounded.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Optional inclusive upper time bound; null keeps the side unbounded.</summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>
/// One Collection-scoped export request. The Collection resolves through the one authoritative
/// Collection catalog (<c>CollectionCatalogService</c>, docs/adr/0009); the scope is its resolved
/// valid membership, and unarchived members are reported explicitly instead of failing or being
/// silently narrowed away (docs/PRD.md FR-23, issue #106).
/// </summary>
public sealed record ExportCollectionRequest
{
    /// <summary>The requested Collection name, exactly as the caller wrote it.</summary>
    public required string CollectionName { get; init; }

    /// <summary>Absolute directory that will receive the export package.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Optional inclusive lower time bound; null keeps the side unbounded.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Optional inclusive upper time bound; null keeps the side unbounded.</summary>
    public DateTimeOffset? Until { get; init; }
}

/// <summary>
/// The requested export scope cannot produce a truthful package: the resolved Collection has no
/// archived conversation to derive a dataset from, or its archived members span more than one
/// account while a package carries exactly one <c>source_account_id</c>. This is a deterministic
/// runtime failure (CLI exit 1, generic <c>failure</c> code), reported with the explicit scope
/// facts — never silently narrowed (docs/PRD.md FR-23, issue #106).
/// </summary>
public sealed class ExportScopeException : Exception
{
    public ExportScopeException(string message) : base(message)
    {
    }
}

/// <summary>
/// The canonical export workflow: <c>Canonical SQLite -&gt; JSONL/YAML/JSON</c>.
/// <para>
/// The canonical archive is the operational system of record; the dataset is derived/interchange
/// state (docs/PRD.md G7/G10, docs/adr/0008-raw-vault-canonical-query-layers.md,
/// docs/EXPORT_PRD.md sections 3.2 and 15). This workflow therefore depends on the exporter, the
/// archive, the Collection catalog and the clock only: it must never reach a source catalog,
/// <see cref="ImportService"/>, <c>ISourceAdapter</c>, live conversation discovery/probing, WeChat
/// key acquisition, capture or Raw Vault ingest. The Collection catalog is the one authoritative
/// scope resolver (docs/adr/0009-collection-configuration-ownership.md) — the same catalog
/// <c>sync --collection</c> and the canonical query surfaces resolve through, so an export scope
/// cannot drift into a second Collection definition (docs/PRD.md FR-23, issue #106).
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
    CollectionCatalogService collections,
    IClock clock)
{
    private readonly IDatasetExporter _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
    private readonly IArchiveStore _archive = archive ?? throw new ArgumentNullException(nameof(archive));
    private readonly CollectionCatalogService _collections = collections ?? throw new ArgumentNullException(nameof(collections));
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Derives one conversation's dataset from the current canonical archive state.
    /// <para>
    /// The conversation must already be archived; otherwise
    /// <see cref="ConversationNotArchivedException"/> is thrown so the caller can report the
    /// deterministic conversation-not-found failure instead of silently reading the live source.
    /// When <paramref name="request"/> carries time bounds, only the records inside the inclusive
    /// range are exported and the package describes exactly that selection.
    /// </para>
    /// </summary>
    public async Task<ExportResult> ExportConversationAsync(
        ExportConversationRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRange(request.Since, request.Until);

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

        var result = await ExportAsync(
            [conversation.Id],
            conversation.AccountId,
            request.OutputDirectory,
            request.Since,
            request.Until,
            new ExportScope
            {
                Kind = "conversation",
                ConversationId = conversation.Id,
                Since = FormatBound(request.Since),
                Until = FormatBound(request.Until),
            },
            diagnostics,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(OperationProgress.Of(
            OperationStages.Completed,
            result.RecordCount,
            result.RecordCount,
            conversation.Id));

        return result;
    }

    /// <summary>
    /// Derives one Collection's dataset from the current canonical archive state
    /// (docs/PRD.md FR-23, issue #106).
    /// <para>
    /// The scope resolves through the one authoritative Collection catalog and is its resolved
    /// valid membership in first-declaration order. A member the canonical archive does not know
    /// contributes no records and is reported explicitly in the result and in
    /// <c>manifest.json</c> instead of failing the export or silently narrowing the scope; the
    /// export still describes exactly what the archive knows. A Collection with no archived
    /// member at all has no dataset to derive and no account to name in the manifest, so it is a
    /// deterministic <see cref="ExportScopeException"/>; archived members spanning more than one
    /// account cannot share a package's single <c>source_account_id</c> and fail the same way.
    /// When <paramref name="request"/> carries time bounds, only the records inside the inclusive
    /// range are exported.
    /// </para>
    /// </summary>
    /// <exception cref="CollectionNotFoundException">The name is not defined.</exception>
    /// <exception cref="CollectionConfigurationException">The configuration is invalid.</exception>
    /// <exception cref="ExportScopeException">
    /// No member is archived, or the archived members belong to more than one account.
    /// </exception>
    public async Task<ExportResult> ExportCollectionAsync(
        ExportCollectionRequest request,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRange(request.Since, request.Until);

        // Resolution is the catalog's contract: an unknown name and an invalid configuration are
        // deterministic failures the caller maps to its transport's error vocabulary.
        var definition = await _collections
            .ResolveAsync(request.CollectionName, cancellationToken)
            .ConfigureAwait(false);

        await _archive.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Membership is consumed in the catalog's resolved order (first-declaration, de-duplicated),
        // so the dataset, its manifest and its conversation order are deterministic for a given
        // archive and configuration state.
        var archived = new List<ArchiveConversation>();
        var unarchived = new List<string>();
        foreach (var memberId in definition.ConversationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var conversation = await _archive
                .GetConversationAsync(memberId, cancellationToken)
                .ConfigureAwait(false);
            if (conversation is null)
            {
                unarchived.Add(memberId);
            }
            else
            {
                archived.Add(conversation);
            }
        }

        if (archived.Count == 0)
        {
            throw new ExportScopeException(
                $"collection '{definition.Name}' has no archived conversations " +
                $"({unarchived.Count} unarchived member(s): {DescribeMembers(unarchived)}); " +
                "sync the Collection before exporting it.");
        }

        // One package carries exactly one manifest source_account_id and one identity catalog, so
        // archived members from different accounts cannot be truthfully described by one package.
        var accountIds = archived
            .Select(conversation => conversation.AccountId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (accountIds.Count > 1)
        {
            throw new ExportScopeException(
                $"collection '{definition.Name}' resolves to archived conversations from " +
                $"{accountIds.Count} accounts ({string.Join(", ", accountIds)}); an export package " +
                "describes one account, so export each account separately.");
        }

        // Manifest diagnostics come from persisted audit state only — the same projection the
        // single-conversation export uses, accumulated per archived member (Issue #66, Gap D).
        var diagnostics = new List<ImportDiagnostic>();
        foreach (var conversation in archived)
        {
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.AddRange(await _archive
                .ReadConversationDiagnosticsAsync(conversation.Id, cancellationToken)
                .ConfigureAwait(false));
        }

        progress?.Report(OperationProgress.Of(
            OperationStages.Exporting,
            archived.Sum(conversation => conversation.MessageCount),
            archived.Sum(conversation => conversation.MessageCount),
            definition.Name));

        var result = await ExportAsync(
            [.. archived.Select(conversation => conversation.Id)],
            accountIds[0],
            request.OutputDirectory,
            request.Since,
            request.Until,
            new ExportScope
            {
                Kind = "collection",
                CollectionName = definition.Name,
                Since = FormatBound(request.Since),
                Until = FormatBound(request.Until),
                MemberConversationCount = definition.ConversationIds.Count,
                ArchivedMemberConversationCount = archived.Count,
                UnarchivedConversationIds = [.. unarchived],
                InvalidMemberCount = definition.InvalidConversationIds.Count,
                DuplicateMemberCount = definition.DuplicateConversationIds.Count,
            },
            diagnostics,
            progress,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(OperationProgress.Of(
            OperationStages.Completed,
            result.RecordCount,
            result.RecordCount,
            definition.Name));

        return result;
    }

    private async Task<ExportResult> ExportAsync(
        IReadOnlyList<string> conversationIds,
        string accountId,
        string outputDirectory,
        DateTimeOffset? since,
        DateTimeOffset? until,
        ExportScope scope,
        IReadOnlyList<ImportDiagnostic> diagnostics,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await _exporter.ExportAsync(
            new ExportRequest
            {
                OutputDirectory = outputDirectory,
                // Account/source metadata is read from archived account state by the exporter.
                AccountId = accountId,
                ConversationIds = conversationIds,
                CreatedAt = _clock.UtcNow,
                Since = since,
                Until = until,
                // The scope travels with the dataset so manifest.json says what the package was
                // selected for, including unarchived Collection members (docs/EXPORT_PRD.md 14).
                Scope = scope,
                // Completeness diagnostics travel with the dataset so a consumer can judge it from
                // manifest.json alone.
                Diagnostics = diagnostics,
            },
            progress is null ? null : new Progress<ExportProgress>(p =>
                progress.Report(OperationProgress.Of(p.Stage, p.Processed, p.Total, p.ConversationId))),
            cancellationToken).ConfigureAwait(false);

        // The result mirrors the manifest: it names the requested scope so a caller can report
        // exactly what the package was selected for, including unarchived Collection members.
        return result with
        {
            Scope = scope,
            Diagnostics = [.. result.Diagnostics, .. diagnostics],
        };
    }

    private static void ValidateRange(DateTimeOffset? since, DateTimeOffset? until)
    {
        if (since is not null && until is not null && since > until)
        {
            throw new ArgumentException("since must not be later than until.", nameof(since));
        }
    }

    private static string? FormatBound(DateTimeOffset? bound) =>
        bound is null
            ? null
            : bound.Value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);

    private static string DescribeMembers(IReadOnlyList<string> memberIds) =>
        memberIds.Count == 0 ? "none" : string.Join(", ", memberIds);
}