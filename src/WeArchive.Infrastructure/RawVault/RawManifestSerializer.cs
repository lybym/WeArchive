using System.Text.Json;
using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>
/// Serialization for the Raw Vault generation manifest. The manifest is an independently
/// versioned persistent format (docs/RAW_VAULT.md): its field names are stable snake_case
/// so a future reader cannot be broken by a C# property rename.
/// </summary>
internal static class RawManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>Serializes the manifest to UTF-8 JSON text.</summary>
    public static string Serialize(RawManifest manifest) =>
        JsonSerializer.Serialize(manifest, Options);

    /// <summary>
    /// Deserializes a manifest and validates its version. Returns null when the document is
    /// not a valid manifest at a supported version, allowing callers to fail closed for a
    /// published generation rather than silently omitting its evidence (Issue #22 invalid-manifest rejection).
    /// </summary>
    public static RawManifest? TryDeserialize(string json)
    {
        RawManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RawManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null)
        {
            return null;
        }

        if (manifest.ManifestVersion is < 1 or > RawManifest.CurrentManifestVersion)
        {
            return null;
        }

        if (manifest.VaultFormatVersion < 1)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.GenerationId) ||
            string.IsNullOrWhiteSpace(manifest.AccountId))
        {
            return null;
        }

        return manifest;
    }
}
