using WeArchive.Core.RawVault;

namespace WeArchive.Core.Abstractions;

/// <summary>
/// Source-independent persistence for Raw Vault generations. docs/RAW_VAULT.md,
/// docs/ARCHITECTURE.md (Raw Vault section).
/// <para>
/// The store treats artifacts as opaque content objects: it records their role, name, size
/// and SHA-256 checksum, but never inspects artifact internals. WeChat schema details must
/// not leak through this interface into Core, CLI, query or export layers
/// (docs/ARCHITECTURE.md adapter boundary rules).
/// </para>
/// <para>
/// Reliability: a generation is only discoverable after <see cref="IRawGenerationSession.PublishAsync"/>
/// writes its manifest (publish-last). A capture that fails or is cancelled discards its
/// staging directory best-effort and publishes nothing. No persistent journal, commit marker
/// or rollback ledger is introduced (Issue #22 non-goals, docs/DEVELOPMENT.md R1).
/// </para>
/// </summary>
public interface IRawVaultStore
{
    string VaultRoot { get; }

    /// <summary>
    /// Creates a staging directory for a new generation and returns a session that writes
    /// artifacts and publishes the manifest. The generation id is derived deterministically
    /// from the context so the store and the caller agree on identity.
    /// </summary>
    Task<IRawGenerationSession> BeginGenerationAsync(
        RawGenerationContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stable account IDs with at least one account directory in the vault, ordered ordinal.
    /// <para>
    /// This lets a caller report capture progress that exists before any canonical ingest, without
    /// learning the vault's physical layout: the returned value is the same stable account ID the
    /// canonical archive uses.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<string>> ListAccountIdsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Published generations for an account, ordered by capture time ascending. A final
    /// generation directory with a missing, unreadable, or invalid manifest fails the listing
    /// with <see cref="System.IO.InvalidDataException"/> instead of being silently omitted;
    /// unpublished staging directories are ignored.
    /// </summary>
    Task<IReadOnlyList<RawGenerationSummary>> ListGenerationsAsync(
        string accountId,
        CancellationToken cancellationToken);

    /// <summary>The most recent published generation for an account, or null.</summary>
    Task<RawGenerationSummary?> GetLatestGenerationAsync(
        string accountId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a published generation for read-only inspection, verifying every artifact's
    /// checksum. Returns null when the generation does not exist or its manifest is invalid.
    /// </summary>
    Task<RawGeneration?> OpenGenerationAsync(
        string accountId,
        string generationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// The staged, publish-last write session of one capture run. Everything written through a
/// session is invisible until <see cref="PublishAsync"/> atomically publishes the manifest;
/// a failed or cancelled run discards staged material instead.
/// <para>
/// The session owns filesystem resources until it is disposed; it must be published or
/// discarded before disposal.
/// </para>
/// </summary>
public interface IRawGenerationSession : IAsyncDisposable
{
    string GenerationId { get; }

    string StagingDirectory { get; }

    /// <summary>
    /// Writes one artifact to the staging area and returns its descriptor with a verified
    /// SHA-256 checksum. The artifact content is treated as opaque by the store.
    /// </summary>
    Task<RawArtifactDescriptor> WriteArtifactAsync(
        string role,
        string name,
        Stream content,
        string? sourceFormat,
        bool isDecrypted,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken);

    /// <summary>Reuse previously verified evidence without reacquiring the live source.</summary>
    Task<RawArtifactDescriptor> ReuseArtifactAsync(
        RawGeneration previous,
        RawArtifactDescriptor artifact,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes the generation: writes the manifest to the staging directory and atomically
    /// renames it to its final location. Before this call nothing is discoverable. A
    /// generation that already exists is never overwritten (immutability guard).
    /// </summary>
    Task<RawGeneration> PublishAsync(
        RawManifest manifest,
        CancellationToken cancellationToken);

    /// <summary>Discards all staged artifacts (best-effort). Called on failure or cancellation.</summary>
    Task DiscardAsync();
}
