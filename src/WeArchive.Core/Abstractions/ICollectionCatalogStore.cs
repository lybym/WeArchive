using WeArchive.Core.Domain;

namespace WeArchive.Core.Abstractions;

/// <summary>Reads the authoritative application-level collection catalog.</summary>
public interface ICollectionCatalogStore
{
    Task<IReadOnlyList<Collection>> ReadAsync(CancellationToken cancellationToken);
}
