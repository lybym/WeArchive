using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WeArchive.Infrastructure.Collections;

/// <summary>
/// The authoritative, user-maintained Collection configuration file.
/// docs/adr/0009-collection-configuration-ownership.md.
/// <para>
/// It reads the one documented <c>collections.yaml</c> semantic shape
/// (docs/HARNESS.md section 8, docs/EXPORT_PRD.md section 8) and never writes it: this Issue adds
/// no interactive Collection editor, and user-maintained configuration must not be regenerated or
/// silently repaired.
/// </para>
/// <para>
/// An absent path or an absent file is an empty catalog. A file that cannot be read or parsed is a
/// <see cref="CollectionConfigurationException"/> rather than a silently empty catalog, so a broken
/// configuration is diagnosed instead of silently disabling the user's Collections.
/// </para>
/// </summary>
internal sealed class YamlCollectionCatalogSource : ICollectionCatalogSource
{
    private readonly string? _path;

    public YamlCollectionCatalogSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _path = null;
            return;
        }

        try
        {
            _path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new CollectionConfigurationException(path, $"the configuration path is not usable: {ex.Message}");
        }
    }

    public string Location => _path ?? string.Empty;

    public async Task<CollectionCatalogDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        if (_path is null || !File.Exists(_path))
        {
            return null;
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CollectionConfigurationException(_path, $"the configuration could not be read: {ex.Message}");
        }

        // An empty file declares no Collections; it is not a malformed document.
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        CollectionConfigurationYaml? document;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            document = deserializer.Deserialize<CollectionConfigurationYaml>(text);
        }
        catch (YamlException ex)
        {
            throw new CollectionConfigurationException(_path, $"the configuration is not valid YAML: {ex.Message}");
        }

        if (document is null)
        {
            return null;
        }

        return new CollectionCatalogDocument
        {
            SchemaVersion = document.SchemaVersion,
            Collections = document.Collections?.ToDictionary(
                entry => entry.Key,
                entry => new CollectionEntryDocument { Conversations = entry.Value?.Conversations },
                StringComparer.Ordinal),
        };
    }
}

/// <summary>
/// The on-disk YAML shape of the authoritative Collection configuration. It is deliberately a
/// separate type from the export package's derived <c>collections.yaml</c> document: the
/// authoritative application configuration is user-maintained and must be diagnosed when invalid,
/// whereas the export catalog is derived state that may be regenerated
/// (docs/EXPORT_PRD.md sections 3.2 and 8).
/// </summary>
internal sealed class CollectionConfigurationYaml
{
    [YamlMember(Alias = "schema_version")]
    public string? SchemaVersion { get; set; }

    [YamlMember(Alias = "collections")]
    public Dictionary<string, CollectionConfigurationEntryYaml>? Collections { get; set; }
}

/// <summary>One Collection entry of the authoritative configuration file.</summary>
internal sealed class CollectionConfigurationEntryYaml
{
    [YamlMember(Alias = "conversations")]
    public List<string>? Conversations { get; set; }
}