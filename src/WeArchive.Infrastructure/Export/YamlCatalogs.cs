using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WeArchive.Infrastructure.Export;

/// <summary>
/// The maintainable identity catalog. docs/EXPORT_PRD.md section 5.
/// <para>
/// <c>display_name</c> is always the generated default (latest remark, otherwise empty).
/// <c>display_name_override</c> is the documented user-maintained hook and is never
/// regenerated, so manual corrections survive re-export.
/// </para>
/// </summary>
internal sealed class IdentityDocument
{
    [YamlMember(Alias = "schema_version", Order = 0)]
    public string SchemaVersion { get; set; } = "1.0";

    [YamlMember(Alias = "users", Order = 1)]
    public Dictionary<string, IdentityEntry> Users { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class IdentityEntry
{
    [YamlMember(Alias = "source_user_id", Order = 0)]
    public string SourceUserId { get; set; } = string.Empty;

    [YamlMember(Alias = "remark", Order = 1)]
    public string Remark { get; set; } = string.Empty;

    [YamlMember(Alias = "nickname", Order = 2)]
    public string Nickname { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name", Order = 3)]
    public string DisplayName { get; set; } = string.Empty;

    [YamlMember(Alias = "display_name_override", Order = 4)]
    public string DisplayNameOverride { get; set; } = string.Empty;

    /// <summary>The value a consumer should show.</summary>
    [YamlIgnore]
    public string Effective =>
        !string.IsNullOrWhiteSpace(DisplayNameOverride) ? DisplayNameOverride
        : !string.IsNullOrWhiteSpace(Remark) ? Remark
        : string.Empty;
}

/// <summary>
/// The conversation catalog. docs/EXPORT_PRD.md section 6.
/// <c>alias</c> is user-maintained and preserved across regeneration.
/// </summary>
internal sealed class ConversationDocument
{
    [YamlMember(Alias = "schema_version", Order = 0)]
    public string SchemaVersion { get; set; } = "1.0";

    [YamlMember(Alias = "conversations", Order = 1)]
    public Dictionary<string, ConversationEntry> Conversations { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class ConversationEntry
{
    [YamlMember(Alias = "type", Order = 0)]
    public string Type { get; set; } = string.Empty;

    [YamlMember(Alias = "current_name", Order = 1)]
    public string CurrentName { get; set; } = string.Empty;

    [YamlMember(Alias = "alias", Order = 2)]
    public string Alias { get; set; } = string.Empty;

    [YamlMember(Alias = "user_id", Order = 3)]
    public string? UserId { get; set; }

    [YamlMember(Alias = "first_message_at", Order = 4)]
    public string? FirstMessageAt { get; set; }

    [YamlMember(Alias = "last_message_at", Order = 5)]
    public string? LastMessageAt { get; set; }
}

/// <summary>
/// Reusable analysis sets. docs/EXPORT_PRD.md section 8.
/// The MVP writes the empty shape but never overwrites a user-maintained file.
/// </summary>
internal sealed class CollectionDocument
{
    [YamlMember(Alias = "schema_version", Order = 0)]
    public string SchemaVersion { get; set; } = "1.0";

    [YamlMember(Alias = "collections", Order = 1)]
    public Dictionary<string, CollectionEntry> Collections { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class CollectionEntry
{
    [YamlMember(Alias = "conversations", Order = 0)]
    public List<string> Conversations { get; set; } = [];
}

/// <summary>Read/write helpers for the maintainable YAML catalogs.</summary>
internal static class YamlCatalogs
{
    public static string Serialize<T>(T document)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.Preserve)
            .Build();
        return serializer.Serialize(document);
    }

    public static T? Deserialize<T>(string yaml)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return null;
        }

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(NullNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<T>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // A hand-edited file that no longer parses must not block an export;
            // the file is regenerated from the archive instead.
            return null;
        }
    }
}
