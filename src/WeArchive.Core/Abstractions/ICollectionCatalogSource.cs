using WeArchive.Core.Collections;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// Reads the one authoritative application-level Collection configuration.
/// docs/adr/0009-collection-configuration-ownership.md.
/// <para>
/// The abstraction keeps the resolution/validation semantics in Core (which performs no I/O and
/// depends on no YAML library) while the Infrastructure layer owns reading the durable file and
/// deserializing its documented <c>collections.yaml</c> shape.
/// </para>
/// </summary>
public interface ICollectionCatalogSource
{
    /// <summary>
    /// Absolute path of the authoritative configuration, reported in diagnostics. Empty when no
    /// configuration location is configured at all.
    /// </summary>
    string Location { get; }

    /// <summary>
    /// Reads the configuration document. Returns <c>null</c> when no configuration exists, because
    /// an absent file is an empty catalog rather than an error.
    /// </summary>
    /// <exception cref="CollectionConfigurationException">
    /// The configuration exists but cannot be read or parsed. The file is never rewritten.
    /// </exception>
    Task<CollectionCatalogDocument?> ReadAsync(CancellationToken cancellationToken);
}