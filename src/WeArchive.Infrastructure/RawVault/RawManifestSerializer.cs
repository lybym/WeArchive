using System.Text.Json;
using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>
/// Serialization for the Raw Vault generation manifest. The manifest is an independently
/// versioned persistent format (docs/RAW_VAULT.md): its field names are stable snake_case
/// so a future reader cannot be broken by a C# property rename.
/// <para>
/// Read-side validation keeps the version-2 coverage/checkpoint pair honest: the checkpoint
/// addresses exactly the coverage entries recorded as <c>captured</c>/<c>reused</c>, so a
/// <c>complete</c> generation may carry <c>unsupported</c> entries that stay out of the
/// checkpoint, while a checkpoint that omits captured evidence, includes non-evidence, or
/// disagrees on a fingerprint or artifact is rejected rather than trusted (Issue #37).
/// </para>
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

        if (manifest.ManifestVersion >= 2)
        {
            if (manifest.Coverage is null || manifest.Coverage.Any(c => string.IsNullOrWhiteSpace(c.PartitionId)) ||
                manifest.Coverage.Select(c => c.PartitionId).Distinct(StringComparer.Ordinal).Count() != manifest.Coverage.Count)
                return null;

            // A complete generation cannot carry unavailable coverage: the verdict would claim an
            // evidence set the run itself recorded as missing (docs/PRD.md FR-20, Issue #37).
            if (manifest.Capture.Completeness == RawGenerationCompleteness.Complete &&
                manifest.Coverage.Any(c => c.Status == RawPartitionStatus.Unavailable))
                return null;

            var checkpoint = manifest.CaptureCheckpoint;
            if (checkpoint is not null && (checkpoint.Version != 1 ||
                checkpoint.GenerationId != manifest.GenerationId ||
                checkpoint.CaptureAdapterFamily != manifest.Capture.CaptureAdapterFamily ||
                checkpoint.CaptureAdapterVersion != manifest.Capture.CaptureAdapterVersion ||
                checkpoint.PartitionFingerprints is null))
                return null;

            if (checkpoint is not null)
            {
                // The checkpoint addresses exactly the captured/reused evidence of this
                // generation. Every captured/reused coverage entry must be present in it and agree
                // on fingerprint plus an artifact of the same generation; captured/reused is the
                // only status the checkpoint may address, so an unsupported or unavailable entry
                // that appears there makes the manifest ambiguous (Issue #37).
                foreach (var entry in manifest.Coverage)
                {
                    var inCheckpoint = checkpoint.PartitionFingerprints
                        .TryGetValue(entry.PartitionId, out var fingerprint);

                    if (entry.Status is RawPartitionStatus.Captured or RawPartitionStatus.Reused)
                    {
                        if (!inCheckpoint ||
                            string.IsNullOrWhiteSpace(entry.SourceFingerprint) ||
                            !string.Equals(fingerprint, entry.SourceFingerprint, StringComparison.Ordinal) ||
                            string.IsNullOrWhiteSpace(entry.ArtifactSha256) ||
                            !manifest.Artifacts.Any(a => a.Sha256 == entry.ArtifactSha256))
                            return null;
                    }
                    else if (inCheckpoint)
                    {
                        return null;
                    }
                }

                // Every checkpoint fingerprint must be addressable by a coverage entry.
                if (checkpoint.PartitionFingerprints.Count != manifest.Coverage.Count(
                        c => c.Status is RawPartitionStatus.Captured or RawPartitionStatus.Reused))
                {
                    return null;
                }
            }
        }

        return manifest;
    }
}
