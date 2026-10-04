using System.Security.Cryptography;
using System.Text;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;

namespace WeArchive.Infrastructure.RawVault;

/// <summary>
/// Filesystem-backed Raw Vault store. Generations are published publish-last: artifacts are
/// staged in a temporary directory and the manifest is written only on
/// <see cref="RawVaultGenerationSession.PublishAsync"/>, which atomically renames the staging
/// directory to its final location. A published generation is never edited in place
/// (immutability guard); a failed or cancelled capture discards its staging directory
/// best-effort.
/// <para>
/// Physical layout (docs/RAW_VAULT.md):
/// <code>
/// &lt;vault-root&gt;/accounts/&lt;account-id&gt;/generations/&lt;generation-id&gt;/
///   manifest.json
///   artifacts/&lt;sha256&gt;&lt;ext&gt;
/// </code>
/// </para>
/// <para>
/// Reliability — R1: normal success publishes one complete generation; caught
/// cancellation/I/O failure discards staged material and publishes nothing. Process crash
/// and OS/power loss are not guaranteed recovery classes. No journal or commit marker is
/// persisted (Issue #22, docs/DEVELOPMENT.md R1).
/// </para>
/// </summary>
public sealed class RawVaultStore : IRawVaultStore
{
    internal const string AccountsFolder = "accounts";
    internal const string GenerationsFolder = "generations";
    internal const string ArtifactsFolder = "artifacts";
    internal const string ManifestFile = "manifest.json";
    internal const string StagingSuffix = ".staging";

    private readonly string _vaultRoot;

    public RawVaultStore(string vaultRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        _vaultRoot = vaultRoot;
        Directory.CreateDirectory(_vaultRoot);
    }

    public string VaultRoot => _vaultRoot;

    /// <summary>
    /// Lists stable account directories present in the preservation store. The returned value is
    /// the stable account ID (a_...) known to the canonical archive, so freshness can report
    /// captured evidence that has not been ingested yet without exposing vault layout
    /// (docs/HARNESS.md section 10).
    /// </summary>
    public Task<IReadOnlyList<string>> ListAccountIdsAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(_vaultRoot, AccountsFolder);
        if (!Directory.Exists(root))
            return Task.FromResult<IReadOnlyList<string>>([]);

        var ids = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .Cast<string>()
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task<IRawGenerationSession> BeginGenerationAsync(
        RawGenerationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var generationId = StableIds.Generation(
            context.AccountId,
            context.CaptureTime,
            context.CaptureAdapterFamily,
            context.CaptureAdapterVersion);

        var generationsDir = GenerationsDirectoryFor(context.AccountId);
        Directory.CreateDirectory(generationsDir);

        // Staging lives beside the final generation directory under a distinct suffix so a
        // crash leaves no half-published generation discoverable.
        var stagingDir = Path.Combine(generationsDir, generationId + StagingSuffix);
        var artifactsDir = Path.Combine(stagingDir, ArtifactsFolder);

        // Remove a stale staging directory from a previous crashed run, best-effort.
        TryDeleteDirectory(stagingDir);
        Directory.CreateDirectory(artifactsDir);

        return Task.FromResult<IRawGenerationSession>(
            new RawVaultGenerationSession(generationId, stagingDir, artifactsDir));
    }

    public async Task<IReadOnlyList<RawGenerationSummary>> ListGenerationsAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var generationsDir = GenerationsDirectoryFor(accountId);
        if (!Directory.Exists(generationsDir))
        {
            return [];
        }

        var summaries = new List<RawGenerationSummary>();
        foreach (var directory in Directory.EnumerateDirectories(generationsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (directory.EndsWith(StagingSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var generationId = Path.GetFileName(directory) ?? directory;
            var manifestPath = Path.Combine(directory, ManifestFile);
            if (!File.Exists(manifestPath))
                throw new InvalidDataException(
                    $"Published Raw Vault generation '{generationId}' has no manifest.");

            RawManifest? manifest;
            try
            {
                manifest = await ReadManifestAsync(manifestPath).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidDataException(
                    $"Published Raw Vault generation '{generationId}' manifest could not be read.", ex);
            }

            if (manifest is null)
                throw new InvalidDataException(
                    $"Published Raw Vault generation '{generationId}' has an invalid or unsupported manifest.");

            summaries.Add(new RawGenerationSummary
            {
                GenerationId = manifest.GenerationId,
                AccountId = manifest.AccountId,
                CaptureTime = manifest.Capture.CaptureTime,
                Completeness = manifest.Capture.Completeness,
                ArtifactCount = manifest.Capture.ArtifactCount,
                EvidenceFingerprint = ComputeEvidenceFingerprint(manifest.Artifacts),
                PreviousGenerationId = manifest.PreviousGenerationId,
            });
        }

        return OrderByLineage(summaries);
    }

    private static IReadOnlyList<RawGenerationSummary> OrderByLineage(IReadOnlyList<RawGenerationSummary> summaries)
    {
        var byId = summaries.ToDictionary(s => s.GenerationId, StringComparer.Ordinal);
        var remaining = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<RawGenerationSummary>(summaries.Count);

        while (remaining.Count > 0)
        {
            var next = remaining
                .Select(id => byId[id])
                .Where(summary => summary.PreviousGenerationId is null
                    || !byId.ContainsKey(summary.PreviousGenerationId)
                    || emitted.Contains(summary.PreviousGenerationId))
                .OrderBy(summary => summary.CaptureTime)
                .ThenBy(summary => summary.GenerationId, StringComparer.Ordinal)
                .FirstOrDefault();

            // A malformed cycle should not hide otherwise published evidence. Break it
            // deterministically; manifest validation remains responsible for rejecting it.
            next ??= remaining.Select(id => byId[id])
                .OrderBy(summary => summary.CaptureTime)
                .ThenBy(summary => summary.GenerationId, StringComparer.Ordinal)
                .First();

            ordered.Add(next);
            remaining.Remove(next.GenerationId);
            emitted.Add(next.GenerationId);
        }

        return ordered;
    }

    public async Task<RawGenerationSummary?> GetLatestGenerationAsync(
        string accountId,
        CancellationToken cancellationToken)
    {
        var generations = await ListGenerationsAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        return generations.Count == 0 ? null : generations[^1];
    }

    private static string ComputeEvidenceFingerprint(IReadOnlyList<RawArtifactDescriptor> artifacts)
    {
        var identity = string.Join("\n", artifacts
            .OrderBy(a => a.Role, StringComparer.Ordinal)
            .ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => $"{a.Role}\0{a.Name}\0{a.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public async Task<RawGeneration?> OpenGenerationAsync(
        string accountId,
        string generationId,
        CancellationToken cancellationToken)
    {
        var generationDir = GenerationDirectory(accountId, generationId);
        var manifestPath = Path.Combine(generationDir, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = await ReadManifestAsync(manifestPath).ConfigureAwait(false);
        if (manifest is null)
        {
            return null;
        }

        var generation = new RawGeneration
        {
            GenerationId = manifest.GenerationId,
            AccountId = manifest.AccountId,
            Manifest = manifest,
            GenerationDirectory = generationDir,
        };

        // Verify logical content through the source-neutral provider. V1 retains historical
        // generation-relative files; v2 must reconstruct from authoritative maps and packs.
        try
        {
            using var provider = RawVaultArtifactProvider.Create(generation);
            provider.VerifyAll(cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }

        return generation;
    }

    /// <summary>Renames the staging directory to its final location (publish-last).</summary>
    internal static void Publish(RawVaultGenerationSession session, RawManifest manifest)
    {
        var generationDir = session.StagingDirectory[..^StagingSuffix.Length];

        // Immutability guard: a published generation is never overwritten.
        if (Directory.Exists(generationDir))
        {
            throw new InvalidOperationException(
                $"Generation '{session.GenerationId}' already exists and cannot be overwritten " +
                "(Raw Vault generations are immutable).");
        }

        var manifestPath = Path.Combine(session.StagingDirectory, ManifestFile);
        File.WriteAllText(manifestPath, RawManifestSerializer.Serialize(manifest));

        // Atomic rename on the same volume: the generation becomes discoverable only here.
        Directory.Move(session.StagingDirectory, generationDir);
    }

    private string AccountDirectory(string accountId) =>
        Path.Combine(_vaultRoot, AccountsFolder, accountId);

    private string GenerationsDirectoryFor(string accountId) =>
        Path.Combine(AccountDirectory(accountId), GenerationsFolder);

    private string GenerationDirectory(string accountId, string generationId) =>
        Path.Combine(GenerationsDirectoryFor(accountId), generationId);

    private static async Task<RawManifest?> ReadManifestAsync(string manifestPath)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        return RawManifestSerializer.TryDeserialize(json);
    }

    /// <summary>
    /// Computes the SHA-256 of a file by streaming it, so large artifacts do not need to be
    /// fully loaded into memory.
    /// </summary>
    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}

/// <summary>
/// The staging session for one capture run. Artifacts are written to the staging directory
/// and the manifest is published atomically on <see cref="PublishAsync"/>.
/// </summary>
internal sealed class RawVaultGenerationSession : IRawGenerationSession
{
    private readonly string _artifactsDir;
    private bool _disposed;

    public RawVaultGenerationSession(string generationId, string stagingDirectory, string artifactsDir)
    {
        GenerationId = generationId;
        StagingDirectory = stagingDirectory;
        _artifactsDir = artifactsDir;
    }

    public string GenerationId { get; }

    public string StagingDirectory { get; }

    public async Task<RawArtifactDescriptor> WriteArtifactAsync(
        string role,
        string name,
        Stream content,
        string? sourceFormat,
        bool isDecrypted,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var extension = Path.GetExtension(name);

        // Write the content to a temp file, then compute its SHA-256 by streaming.
        var tempPath = Path.Combine(_artifactsDir, "." + Guid.NewGuid().ToString("n") + ".tmp");
        long size;
        await using (var fileStream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous))
        {
            await content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
            size = fileStream.Length;
        }

        var hash = await RawVaultStore.ComputeSha256Async(tempPath, cancellationToken)
            .ConfigureAwait(false);

        var artifactFileName = hash + extension;
        var finalPath = Path.Combine(_artifactsDir, artifactFileName);

        // If identical content was already written, the temp file is a duplicate; remove it.
        if (File.Exists(finalPath))
        {
            TryDeleteFile(tempPath);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }

        return new RawArtifactDescriptor
        {
            Role = role,
            Name = name,
            ContentRef = $"{RawVaultStore.ArtifactsFolder}/{artifactFileName}",
            Sha256 = hash,
            Size = size,
            SourceFormat = sourceFormat,
            IsDecrypted = isDecrypted,
            Metadata = metadata,
        };
    }

    public async Task<RawArtifactDescriptor> ReuseArtifactAsync(
        RawGeneration previous,
        RawArtifactDescriptor artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(artifact);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!previous.Manifest.Artifacts.Contains(artifact))
            throw new InvalidDataException("Reused artifact is absent from the verified generation.");

        var contentRef = artifact.ContentRef
            ?? throw new InvalidDataException("A vault-format-v1 writer cannot reuse an artifact without content_ref.");
        var source = Path.GetFullPath(Path.Combine(previous.GenerationDirectory, contentRef));
        var sourceRoot = Path.GetFullPath(previous.GenerationDirectory) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reused artifact path escapes its generation.");
        var name = Path.GetFileName(source);
        var destination = Path.Combine(_artifactsDir, name);
        if (!File.Exists(destination))
            File.Copy(source, destination);
        var actual = await RawVaultStore.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reused artifact checksum changed.");
        return artifact;
    }

    public Task<RawGeneration> PublishAsync(RawManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        RawVaultStore.Publish(this, manifest);

        var generationDir = StagingDirectory[..^RawVaultStore.StagingSuffix.Length];
        return Task.FromResult(new RawGeneration
        {
            GenerationId = manifest.GenerationId,
            AccountId = manifest.AccountId,
            Manifest = manifest,
            GenerationDirectory = generationDir,
        });
    }

    public Task DiscardAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        RawVaultStore.TryDeleteDirectory(StagingDirectory);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        // If the session was never published, clean up the staging directory best-effort.
        if (Directory.Exists(StagingDirectory))
        {
            RawVaultStore.TryDeleteDirectory(StagingDirectory);
        }

        return ValueTask.CompletedTask;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort.
        }
    }
}
