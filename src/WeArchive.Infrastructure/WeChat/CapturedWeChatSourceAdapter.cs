using System.Runtime.Versioning;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat.Compatibility;

namespace WeArchive.Infrastructure.WeChat;

/// <summary>
/// Builds the existing WeChat 4.x reader over one verified Raw Vault generation. This path
/// has no discovery or key-acquisition fallback: the only database paths come from manifest
/// artifacts and the reader's cache accepts plaintext images only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CapturedWeChatSourceAdapter
{
    public static WeChatWindowsSourceAdapter Create(RawGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        var artifacts = RawVaultArtifactProvider.Create(generation);
        try
        {
            return CreateCore(generation, artifacts);
        }
        catch
        {
            artifacts.Dispose();
            throw;
        }
    }

    private static WeChatWindowsSourceAdapter CreateCore(RawGeneration generation, RawVaultArtifactProvider artifacts)
    {
        var manifest = generation.Manifest;
        if (manifest.AccountId != generation.AccountId
            || manifest.GenerationId != generation.GenerationId
            || string.IsNullOrWhiteSpace(manifest.SourceProfileId))
            throw new InvalidDataException("Raw Vault generation identity does not match its manifest.");

        if ((manifest.ManifestVersion, manifest.VaultFormatVersion) is not ((1, 1) or (2, 1) or (3, 2)))
            throw new NotSupportedException("No captured-source reader supports this Raw Vault manifest/format version.");

        if (!string.Equals(manifest.Capture.CaptureAdapterFamily, WeChatCaptureAdapter.Family, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Source.SourceVersion)
            || !manifest.Source.SourceVersion.StartsWith("4.", StringComparison.Ordinal))
            throw new NotSupportedException("No captured-source reader supports this Raw Vault source family/version.");

        if (manifest.Capture.Completeness != RawGenerationCompleteness.Complete)
        {
            // The completeness gate is the source-neutral R2 rule, so the refusal carries the
            // source-neutral coverage rollup instead of a bare string: ingest, sync and rebuild
            // all fail closed, and a machine caller can read what was missing without opening the
            // manifest (docs/PRD.md FR-20, docs/RAW_VAULT.md section 7, Issue #51).
            var completeness = manifest.Capture.Completeness.ToString().ToLowerInvariant();
            throw new IncompleteCanonicalCoverageException(
                CanonicalCoverage.From(manifest),
                $"Raw Vault generation '{manifest.GenerationId}' is {completeness}; a partial or " +
                "incomplete Raw Vault generation cannot establish a complete canonical rebuild.");
        }

        var databases = manifest.Artifacts
            .Where(a => a.Role == "source-database"
                && string.Equals(a.SourceFormat, "sqlite", StringComparison.OrdinalIgnoreCase)
                && a.Metadata is not null
                && (a.IsDecrypted
                    || (a.Metadata.TryGetValue("was_plaintext", out var wasPlaintext)
                        && string.Equals(wasPlaintext, "true", StringComparison.OrdinalIgnoreCase)))
                && a.Metadata.ContainsKey("source_relative_path"))
            .Select(a =>
            {
                var relative = a.Metadata!["source_relative_path"].Replace('\\', '/');
                return (Relative: relative, Path: artifacts.GetVerifiedPath(a));
            }).ToList();

        string? Find(string suffix) => databases
            .Where(d => d.Relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Path)
            .FirstOrDefault();
        var session = Find("session/session.db");
        var contacts = Find("contact/contact.db");
        // Conversation message tables live in both the message_N and biz_message_N families
        // (official-account conversations live in the latter), so the captured reader indexes
        // every preserved shard the live locator recognises as a message shard (Issue #37).
        var messages = databases
            .Where(d => WeChatDataLocator.IsMessageShardFileName(Path.GetFileName(d.Relative)))
            .OrderBy(d => d.Relative, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (session is null || contacts is null || messages.Count == 0)
            throw new InvalidDataException("The Raw Vault generation is missing required WeChat 4.x database artifacts.");

        var paths = messages.Select(d => d.Path).ToArray();
        var partitions = messages.ToDictionary(d => d.Path, d => Path.GetFileNameWithoutExtension(d.Relative), StringComparer.OrdinalIgnoreCase);
        var account = new WeChatAccountLocation(
            manifest.SourceProfileId,
            generation.GenerationDirectory,
            generation.GenerationDirectory,
            manifest.Capture.CaptureTime);
        var cache = SqlCipherDatabaseCache.ForCapturedPlaintext();
        var reader = new WeChatAccountReader(
            account,
            cache,
            session,
            contacts,
            paths,
            partitions,
            requiredMessageEvidenceComplete: IsRequiredMessageEvidenceComplete(manifest));
        var sourceAccount = new SourceAccount
        {
            SourceProfileId = manifest.SourceProfileId,
            DisplayName = manifest.SourceProfileId,
            DataRootPath = null,
            LastActiveAt = manifest.Capture.CaptureTime,
            IsCurrent = true,
        };
        var descriptor = new SourceDescriptor
        {
            AdapterName = manifest.Source.AdapterName,
            AdapterVersion = manifest.Source.AdapterVersion,
            SourceVersion = manifest.Source.SourceVersion,
            SourceProductName = manifest.Source.SourceProductName,
            IsAvailable = true,
        };
        return new WeChatWindowsSourceAdapter(sourceAccount, descriptor, reader, cache, artifacts);
    }

    /// <summary>
    /// Whether this generation's manifest proves that every Required message-bearing partition it
    /// accounts for was captured or reused.
    /// <para>
    /// Only such a generation may report a conversation with no message table as legitimately
    /// empty. A version-1 manifest, a generation whose coverage does not name any Required message
    /// shard, or a Required message shard recorded as unavailable/unsupported leaves the reader's
    /// existing Fatal source-coverage semantics in place, so a conversation is never published as
    /// complete while required evidence could actually be missing (docs/RAW_VAULT.md section 7,
    /// Issue #37). Which partitions are Required is a WeChat source-partition question, so the
    /// judgement stays here rather than in the source-neutral reader.
    /// </para>
    /// <para>
    /// Coverage alone is not proof: the counted shards are cross-checked against the manifest's
    /// capture checkpoint, mirroring the identity checks <see cref="WeChatCaptureAdapter.BuildPriorMap"/>
    /// applies to a reused predecessor, so a <c>complete</c> version-2 coverage shape without
    /// checkpoint evidence — or with checkpoint evidence that disagrees with its own coverage —
    /// is not accepted either (Issue #39). This shape is not reachable through the shipped capture
    /// path; the cross-check is hardening for hand-built or future manifests.
    /// </para>
    /// </summary>
    private static bool IsRequiredMessageEvidenceComplete(RawManifest manifest)
    {
        if (manifest.ManifestVersion < 2 || manifest.Coverage.Count == 0)
        {
            return false;
        }

        // The checkpoint is the capture cursor published with this manifest. It must belong to
        // this generation and this capture, and every counted shard must appear in it with the
        // same source fingerprint its coverage entry records.
        var checkpoint = manifest.CaptureCheckpoint;
        if (checkpoint is null
            || checkpoint.Version != 1
            || !string.Equals(checkpoint.GenerationId, manifest.GenerationId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.CaptureAdapterFamily, manifest.Capture.CaptureAdapterFamily, StringComparison.Ordinal)
            || !string.Equals(checkpoint.CaptureAdapterVersion, manifest.Capture.CaptureAdapterVersion, StringComparison.Ordinal))
        {
            return false;
        }

        var requiredMessageShards = manifest.Coverage
            .Where(entry =>
                WeChatSourcePartitionPolicy.Classify(entry.PartitionId) == WeChatSourcePartitionClass.Required &&
                WeChatDataLocator.IsMessageShardFileName(Path.GetFileName(entry.PartitionId)))
            .ToArray();

        return requiredMessageShards.Length > 0 &&
            requiredMessageShards.All(entry =>
                entry.Status is RawPartitionStatus.Captured or RawPartitionStatus.Reused &&
                entry.SourceFingerprint is not null &&
                checkpoint.PartitionFingerprints.TryGetValue(entry.PartitionId, out var fingerprint) &&
                string.Equals(fingerprint, entry.SourceFingerprint, StringComparison.Ordinal));
    }
}
