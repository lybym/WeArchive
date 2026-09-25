using System.Runtime.Versioning;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
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
        var manifest = generation.Manifest;
        if (manifest.AccountId != generation.AccountId
            || manifest.GenerationId != generation.GenerationId
            || string.IsNullOrWhiteSpace(manifest.SourceProfileId))
            throw new InvalidDataException("Raw Vault generation identity does not match its manifest.");

        if (manifest.ManifestVersion is < 1 or > RawManifest.CurrentManifestVersion
            || manifest.VaultFormatVersion != RawManifest.CurrentVaultFormatVersion)
            throw new NotSupportedException("No captured-source reader supports this Raw Vault manifest/format version.");

        if (!string.Equals(manifest.Capture.CaptureAdapterFamily, WeChatCaptureAdapter.Family, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.Source.SourceVersion)
            || !manifest.Source.SourceVersion.StartsWith("4.", StringComparison.Ordinal))
            throw new NotSupportedException("No captured-source reader supports this Raw Vault source family/version.");

        if (manifest.Capture.Completeness != RawGenerationCompleteness.Complete)
            throw new InvalidDataException("A partial or incomplete Raw Vault generation cannot establish a complete canonical rebuild.");

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
                var path = Path.GetFullPath(Path.Combine(generation.GenerationDirectory, a.ContentRef));
                var root = Path.GetFullPath(generation.GenerationDirectory) + Path.DirectorySeparatorChar;
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                    throw new InvalidDataException("A Raw Vault artifact path is invalid or missing.");
                return (Relative: relative, Path: path);
            }).ToList();

        string? Find(string suffix) => databases
            .Where(d => d.Relative.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Path)
            .FirstOrDefault();
        var session = Find("session/session.db");
        var contacts = Find("contact/contact.db");
        var messages = databases
            .Where(d => Path.GetFileName(d.Relative).StartsWith("message_", StringComparison.OrdinalIgnoreCase))
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
        var reader = new WeChatAccountReader(account, cache, session, contacts, paths, partitions);
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
        return new WeChatWindowsSourceAdapter(sourceAccount, descriptor, reader, cache);
    }
}
