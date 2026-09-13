using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Services;

/// <summary>
/// Read-only discovery of local sources, independent of any client-specific format.
/// docs/PRD.md FR-01/FR-03, docs/ARCHITECTURE.md section 3.2.
/// </summary>
public sealed class SourceCatalogService(ISourceAdapter adapter)
{
    private readonly ISourceAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    public string AdapterName => _adapter.AdapterName;

    public string AdapterVersion => _adapter.AdapterVersion;

    public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
        _adapter.DescribeSourceAsync(cancellationToken);

    public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
        _adapter.ListAccountsAsync(cancellationToken);

    public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
        string sourceProfileId,
        CancellationToken cancellationToken) =>
        _adapter.ListConversationsAsync(sourceProfileId, cancellationToken);

    public Task<SourceConversationDetail> DescribeConversationAsync(
        string sourceProfileId,
        string sourceConversationId,
        CancellationToken cancellationToken) =>
        _adapter.DescribeConversationAsync(sourceProfileId, sourceConversationId, cancellationToken);
}
