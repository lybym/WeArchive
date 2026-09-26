using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

namespace WeArchive.Infrastructure.Collections;

/// <summary>Reads user-owned application configuration without rewriting it.</summary>
public sealed class YamlCollectionCatalogStore(string path) : ICollectionCatalogStore
{
    private readonly string _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));

    public async Task<IReadOnlyList<Collection>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        var yaml = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        try
        {
            var document = new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .Build()
                .Deserialize<CollectionDocument>(yaml);
            if (document is null || document.SchemaVersion != "1.0" || document.Collections is null)
                throw new InvalidDataException("Collection catalog must define schema_version '1.0' and a collections mapping.");
            return document.Collections.Select(pair => new Collection
            {
                Name = pair.Key,
                ConversationIds = pair.Value?.Conversations ?? throw new InvalidDataException($"Collection '{pair.Key}' has no conversations list."),
            }).ToArray();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException($"Collection catalog '{_path}' is invalid YAML: {ex.Message}", ex);
        }
    }

    private sealed class CollectionDocument
    {
        [YamlMember(Alias = "schema_version", Order = 0)]
        public string? SchemaVersion { get; set; }

        [YamlMember(Alias = "collections", Order = 1)]
        public Dictionary<string, CollectionEntry>? Collections { get; set; }
    }

    private sealed class CollectionEntry
    {
        [YamlMember(Alias = "conversations", Order = 0)]
        public List<string>? Conversations { get; set; }
    }
}
