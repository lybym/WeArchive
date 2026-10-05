using System.Security.Cryptography;
using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>
/// Resolves a logical artifact descriptor without exposing its physical storage layout to
/// consumers. V1 reads generation-relative files; v2 traverses its authoritative map and packs.
/// </summary>
internal sealed class RawVaultArtifactProvider : IDisposable
{
    private readonly RawGeneration _generation;
    private readonly RawVaultV2PackStore? _v2Store;
    private readonly string? _materializationRoot;
    private bool _disposed;

    internal string? MaterializationRoot => _materializationRoot;

    private RawVaultArtifactProvider(RawGeneration generation)
    {
        _generation = generation;
        var tuple = (generation.Manifest.ManifestVersion, generation.Manifest.VaultFormatVersion);
        if (tuple is (1, 1) or (2, 1))
            return;
        if (tuple != (3, 2))
            throw new NotSupportedException("No Raw Vault artifact provider supports this manifest/vault-format version.");

        var accountDirectory = Path.GetFullPath(Path.Combine(generation.GenerationDirectory, "..", ".."));
        _v2Store = new RawVaultV2PackStore(accountDirectory);
        _materializationRoot = Path.Combine(Path.GetTempPath(), "WeArchive", "raw-vault-materialized", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_materializationRoot);
    }

    internal static RawVaultArtifactProvider Create(RawGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return new RawVaultArtifactProvider(generation);
    }

    internal string GetVerifiedPath(RawArtifactDescriptor artifact, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_generation.Manifest.Artifacts.Contains(artifact))
            throw new InvalidDataException("The artifact is not part of this Raw Vault generation.");

        if (_generation.Manifest.VaultFormatVersion == 1)
        {
            var path = ResolveV1Path(artifact);
            VerifyFile(path, artifact, cancellationToken);
            return path;
        }

        var storage = artifact.Storage ?? throw new InvalidDataException("A v2 artifact is missing its storage descriptor.");
        var pathV2 = Path.Combine(_materializationRoot!, Guid.NewGuid().ToString("N") + ".artifact");
        try
        {
            using (var output = new FileStream(pathV2, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                _v2Store!.MaterializeTo(new RawVaultV2Format.Artifact(
                    Convert.FromHexString(storage.Root), artifact.Size, storage.BlockCount, artifact.Sha256, [], storage.BlockSize),
                    output,
                    cancellationToken);
                output.Flush(flushToDisk: true);
            }

            var info = new FileInfo(pathV2);
            if (info.Length != artifact.Size)
                throw new InvalidDataException("Raw Vault materialized artifact size does not match its descriptor.");
            return pathV2;
        }
        catch
        {
            TryDelete(pathV2);
            throw;
        }
    }

    internal void VerifyAll(CancellationToken cancellationToken)
    {
        foreach (var artifact in _generation.Manifest.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_generation.Manifest.VaultFormatVersion == 1)
            {
                VerifyFile(ResolveV1Path(artifact), artifact, cancellationToken);
                continue;
            }

            var storage = artifact.Storage ?? throw new InvalidDataException("A v2 artifact is missing its storage descriptor.");
            _v2Store!.MaterializeTo(new RawVaultV2Format.Artifact(
                Convert.FromHexString(storage.Root), artifact.Size, storage.BlockCount, artifact.Sha256, [], storage.BlockSize),
                Stream.Null,
                cancellationToken);
        }
    }

    private string ResolveV1Path(RawArtifactDescriptor artifact)
    {
        var contentRef = artifact.ContentRef ?? throw new InvalidDataException("A v1 artifact has no content_ref.");
        var root = Path.GetFullPath(_generation.GenerationDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_generation.GenerationDirectory, contentRef));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("A Raw Vault artifact path is invalid or missing.");
        return path;
    }

    private static void VerifyFile(string path, RawArtifactDescriptor artifact, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length != artifact.Size)
            throw new InvalidDataException("Raw Vault artifact size does not match its descriptor.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        cancellationToken.ThrowIfCancellationRequested();
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(actual, artifact.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Raw Vault artifact SHA-256 does not match its descriptor.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_materializationRoot is not null)
        {
            try { Directory.Delete(_materializationRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
