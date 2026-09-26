using WeArchive.Core.Abstractions;

namespace WeArchive.Core.Collections;

/// <summary>
/// The shared Collection catalog and resolution service. docs/HARNESS.md section 8,
/// docs/PRD.md FR-23, docs/adr/0009-collection-configuration-ownership.md.
/// <para>
/// It answers exactly two product questions: which Collections exist, and what stable conversation
/// membership one named Collection has. Every scope that accepts a Collection (today
/// <c>sync --collection</c>; later query/search and export) resolves through this service, so a
/// Collection cannot drift into a second, scope-specific definition.
/// </para>
/// <para>
/// The service is source-independent and does no I/O itself: it reads through
/// <see cref="ICollectionCatalogSource"/> and validates/resolves with
/// <see cref="CollectionCatalog.Parse"/>. Invalid configuration is diagnosed, never silently
/// rewritten.
/// </para>
/// </summary>
public sealed class CollectionCatalogService(ICollectionCatalogSource source)
{
    private readonly ICollectionCatalogSource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Absolute path of the authoritative configuration, for diagnostics.</summary>
    public string ConfigurationLocation => _source.Location;

    /// <summary>Loads and validates the whole catalog. An absent configuration yields an empty catalog.</summary>
    /// <exception cref="CollectionConfigurationException">The configuration is invalid.</exception>
    public async Task<CollectionCatalog> LoadAsync(CancellationToken cancellationToken)
    {
        var document = await _source.ReadAsync(cancellationToken).ConfigureAwait(false);
        return CollectionCatalog.Parse(document, _source.Location);
    }

    /// <summary>Resolves one named Collection to its stable conversation membership.</summary>
    /// <exception cref="CollectionConfigurationException">The configuration is invalid.</exception>
    /// <exception cref="CollectionNotFoundException">The name is not defined.</exception>
    public async Task<CollectionDefinition> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var catalog = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return catalog.Find(name)
            ?? throw new CollectionNotFoundException(name, catalog.ConfigurationLocation);
    }
}