namespace WeArchive.Core.Collections;

/// <summary>
/// One named, reusable set of stable conversation IDs. docs/HARNESS.md section 8,
/// docs/PRD.md FR-23.
/// <para>
/// A Collection is the single reusable scope abstraction for sync (and, later, query/search and
/// export). It is deliberately not a <c>sync-group</c>, <c>watch-list</c> or <c>harness-dataset</c>:
/// those would be overlapping concepts for the same conversation scope.
/// </para>
/// <para>
/// Membership keys are stable conversation IDs (<c>g_&lt;16 hex&gt;</c> / <c>u_&lt;16 hex&gt;</c>,
/// docs/DATA_MODEL.md section 16), never mutable display names or upstream ids, so a Collection
/// keeps meaning across renames and re-imports.
/// </para>
/// </summary>
public sealed record CollectionDefinition
{
    /// <summary>The Collection name, exactly as declared by the user.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Every membership entry exactly as declared, in declaration order. The configured file is
    /// never rewritten, so this preserves what the user wrote, including invalid or duplicate
    /// entries.
    /// </summary>
    public required IReadOnlyList<string> DeclaredConversationIds { get; init; }

    /// <summary>
    /// Resolved membership: the valid stable conversation IDs in first-declaration order with
    /// duplicates removed. This is the authoritative membership a scope consumes.
    /// </summary>
    public required IReadOnlyList<string> ConversationIds { get; init; }

    /// <summary>
    /// Declared entries that are not a valid stable conversation ID, in declaration order.
    /// Reported, never silently dropped or rewritten.
    /// </summary>
    public IReadOnlyList<string> InvalidConversationIds { get; init; } = [];

    /// <summary>
    /// Declared entries that repeat an earlier valid membership entry, in declaration order.
    /// Reported, never silently dropped or rewritten.
    /// </summary>
    public IReadOnlyList<string> DuplicateConversationIds { get; init; } = [];
}

/// <summary>
/// The resolved Collection catalog: every Collection in the one authoritative configuration
/// source, plus that source's location for diagnostics. See
/// <c>docs/adr/0009-collection-configuration-ownership.md</c>.
/// </summary>
public sealed record CollectionCatalog
{
    /// <summary>The only collection configuration schema this build understands.</summary>
    public const string SupportedSchemaVersion = "1.0";

    /// <summary>Absolute path of the authoritative configuration file (empty when unconfigured).</summary>
    public required string ConfigurationLocation { get; init; }

    /// <summary>Collections in declaration order.</summary>
    public required IReadOnlyList<CollectionDefinition> Collections { get; init; }

    /// <summary>Resolves a Collection by name (ordinal, case-sensitive) or returns null.</summary>
    public CollectionDefinition? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var collection in Collections)
        {
            if (string.Equals(collection.Name, name, StringComparison.Ordinal))
            {
                return collection;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a stable conversation id
    /// (<c>g_</c>/<c>u_</c> + 16 lowercase hex characters). Group conversations are <c>g_</c>;
    /// every other kind is the peer's <c>u_</c> identity, so those two prefixes cover every
    /// conversation a Collection can name. The check is deliberately exact: the configuration is
    /// reported rather than silently normalized, so an uppercase or truncated id never resolves to
    /// a different conversation than the one the user wrote.
    /// </summary>
    public static bool IsStableConversationId(string? value)
    {
        if (value is null || value.Length != 18)
        {
            return false;
        }

        if (value[0] is not ('g' or 'u') || value[1] != '_')
        {
            return false;
        }

        for (var i = 2; i < value.Length; i++)
        {
            var c = value[i];
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Parses a configuration document into a validated catalog.
    /// <para>
    /// Validation is deterministic and never rewrites the user's configuration: an unsupported
    /// schema version, a missing <c>collections</c> mapping or an empty Collection name is a
    /// configuration failure, while an invalid or duplicated membership entry is reported on the
    /// Collection itself.
    /// </para>
    /// </summary>
    /// <param name="document">The parsed document, or null when no configuration exists.</param>
    /// <param name="configurationLocation">The source location, used only for diagnostics.</param>
    public static CollectionCatalog Parse(
        CollectionCatalogDocument? document,
        string configurationLocation)
    {
        ArgumentNullException.ThrowIfNull(configurationLocation);

        // An absent configuration is an empty catalog, not an error: a user who has not created a
        // Collection yet gets an empty list, and resolving a name is a deterministic unknown-name
        // failure rather than a crash.
        if (document is null)
        {
            return new CollectionCatalog
            {
                ConfigurationLocation = configurationLocation,
                Collections = [],
            };
        }

        if (!string.IsNullOrWhiteSpace(document.SchemaVersion)
            && !string.Equals(document.SchemaVersion, SupportedSchemaVersion, StringComparison.Ordinal))
        {
            throw new CollectionConfigurationException(
                configurationLocation,
                $"unsupported collection schema_version '{document.SchemaVersion}'; this build supports '{SupportedSchemaVersion}'.");
        }

        if (document.Collections is null)
        {
            throw new CollectionConfigurationException(
                configurationLocation,
                "the configuration is missing the required top-level 'collections' mapping.");
        }

        var collections = new List<CollectionDefinition>(document.Collections.Count);
        foreach (var (name, entry) in document.Collections)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CollectionConfigurationException(
                    configurationLocation,
                    "a Collection name is empty.");
            }

            collections.Add(Resolve(name, entry?.Conversations));
        }

        return new CollectionCatalog
        {
            ConfigurationLocation = configurationLocation,
            Collections = collections,
        };
    }

    private static CollectionDefinition Resolve(string name, IReadOnlyList<string>? declared)
    {
        var declaredIds = declared ?? [];
        var membership = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var invalid = new List<string>();
        var duplicates = new List<string>();

        foreach (var entry in declaredIds)
        {
            if (!IsStableConversationId(entry))
            {
                invalid.Add(entry ?? string.Empty);
                continue;
            }

            if (!seen.Add(entry))
            {
                duplicates.Add(entry);
                continue;
            }

            membership.Add(entry);
        }

        return new CollectionDefinition
        {
            Name = name,
            DeclaredConversationIds = declaredIds,
            ConversationIds = membership,
            InvalidConversationIds = invalid,
            DuplicateConversationIds = duplicates,
        };
    }
}

/// <summary>
/// The parsed shape of the authoritative Collection configuration. It mirrors the documented
/// <c>collections.yaml</c> semantics (docs/HARNESS.md section 8, docs/EXPORT_PRD.md section 8) so
/// the durable configuration format is shared rather than reinvented. Deserialization lives in the
/// Infrastructure layer, which is the only layer allowed to depend on a YAML library.
/// </summary>
public sealed class CollectionCatalogDocument
{
    public string? SchemaVersion { get; set; }

    public Dictionary<string, CollectionEntryDocument>? Collections { get; set; }
}

/// <summary>One Collection entry in the configuration document.</summary>
public sealed class CollectionEntryDocument
{
    public List<string>? Conversations { get; set; }
}

/// <summary>
/// The Collection configuration exists but is not valid. It is never silently repaired or
/// rewritten: the caller reports the location and reason and the user fixes the file.
/// </summary>
public sealed class CollectionConfigurationException : Exception
{
    public CollectionConfigurationException(string location, string message)
        : base(string.IsNullOrEmpty(location) ? message : $"{location}: {message}")
    {
        Location = location;
        Reason = message;
    }

    /// <summary>Absolute path of the invalid configuration file.</summary>
    public string Location { get; }

    /// <summary>The validation failure without the location prefix.</summary>
    public string Reason { get; }
}

/// <summary>A requested Collection name is not defined by the authoritative configuration.</summary>
public sealed class CollectionNotFoundException : Exception
{
    public CollectionNotFoundException(string name, string location)
        : base(string.IsNullOrEmpty(location)
            ? $"collection '{name}' is not defined."
            : $"collection '{name}' is not defined in {location}.")
    {
        Name = name;
        Location = location;
    }

    /// <summary>The requested Collection name.</summary>
    public string Name { get; }

    /// <summary>Absolute path of the authoritative configuration file (empty when unconfigured).</summary>
    public string Location { get; }
}