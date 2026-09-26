using System.Text.RegularExpressions;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;

namespace WeArchive.Core.Services;

/// <summary>Validates and resolves the shared, source-independent Collection catalog.</summary>
public sealed class CollectionCatalogService(ICollectionCatalogStore store)
{
    private static readonly Regex StableConversationId = new(@"\A[ug]_[0-9a-f]{16}\z", RegexOptions.CultureInvariant);
    private readonly ICollectionCatalogStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<Collection>> ListAsync(CancellationToken cancellationToken)
    {
        var collections = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collection in collections)
        {
            if (string.IsNullOrWhiteSpace(collection.Name) || !names.Add(collection.Name))
                throw new InvalidDataException($"Collection catalog contains a missing or duplicate name '{collection.Name}'.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in collection.ConversationIds)
            {
                if (id is null || !StableConversationId.IsMatch(id))
                    throw new InvalidDataException($"Collection '{collection.Name}' contains invalid stable conversation ID '{id}'.");
                if (!ids.Add(id))
                    throw new InvalidDataException($"Collection '{collection.Name}' contains duplicate conversation ID '{id}'.");
            }
        }
        return collections.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<Collection> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var collection = (await ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
        return collection ?? throw new KeyNotFoundException($"Collection '{name}' was not found.");
    }
}
