namespace WeArchive.Core.RawVault;

/// <summary>
/// The persisted generation manifest. This is the single source of truth for one published
/// Raw Vault generation: it records the source product/version, the capture adapter, the
/// capture time, the completeness verdict and every artifact with its SHA-256 checksum.
/// <para>
/// The manifest format is independently versioned from the canonical SQLite archive, the
/// message schema and the export schema (docs/DATA_MODEL.md section 18, docs/RAW_VAULT.md).
/// A generation is only discoverable once its manifest has been published; a capture that
/// failed or was cancelled leaves no manifest, so an incomplete snapshot is never mistaken
/// for a complete generation (Issue #22 acceptance criteria).
/// </para>
/// </summary>
public sealed record RawManifest
{
    /// <summary>
    /// The manifest schema version. Increment only when the manifest's own structure changes;
    /// it is independent of the canonical SQLite, message-schema and export-schema versions.
    /// </summary>
    public const int CurrentManifestVersion = 2;

    public const int CurrentVaultFormatVersion = 1;

    public int ManifestVersion { get; init; } = CurrentManifestVersion;

    /// <summary>
    /// The Raw Vault physical format version carried by this generation. Independent of
    /// <see cref="ManifestVersion"/>: the manifest may evolve without changing how artifacts
    /// are laid out on disk, and vice versa.
    /// </summary>
    public int VaultFormatVersion { get; init; } = CurrentVaultFormatVersion;

    public required string GenerationId { get; init; }

    public required string AccountId { get; init; }

    public required string SourceProfileId { get; init; }

    public required RawManifestSource Source { get; init; }

    public required RawManifestCapture Capture { get; init; }

    public required IReadOnlyList<RawArtifactDescriptor> Artifacts { get; init; }

    public IReadOnlyList<RawManifestDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Explicit source partition coverage for this logical generation.</summary>
    public IReadOnlyList<RawPartitionCoverage> Coverage { get; init; } = [];

    /// <summary>Capture progress published with this generation, independent of canonical ingest.</summary>
    public RawCaptureCheckpoint? CaptureCheckpoint { get; init; }

    /// <summary>
    /// The immediately preceding published generation for the same account, or null when this
    /// is the first generation. Forms an append-only chain so later captures never edit earlier
    /// generations (Issue #22 immutability acceptance criterion).
    /// </summary>
    public string? PreviousGenerationId { get; init; }
}

public enum RawPartitionStatus { Captured, Reused, Unavailable, Unsupported }

public sealed record RawPartitionCoverage
{
    public required string PartitionId { get; init; }
    public required RawPartitionStatus Status { get; init; }
    public string? SourceFingerprint { get; init; }
    public string? ArtifactSha256 { get; init; }
    public string? Diagnostic { get; init; }
}

/// <summary>Versioned Raw Vault capture cursor. It is part of the publish-last manifest.</summary>
public sealed record RawCaptureCheckpoint
{
    public int Version { get; init; } = 1;
    public required string GenerationId { get; init; }
    public required string CaptureAdapterFamily { get; init; }
    public required string CaptureAdapterVersion { get; init; }
    public required IReadOnlyDictionary<string, string> PartitionFingerprints { get; init; }
}

/// <summary>Source product/adapter evidence recorded in the manifest.</summary>
public sealed record RawManifestSource
{
    public required string AdapterName { get; init; }

    public required string AdapterVersion { get; init; }

    public string? SourceProductName { get; init; }

    public string? SourceVersion { get; init; }
}

/// <summary>Capture provenance recorded in the manifest.</summary>
public sealed record RawManifestCapture
{
    /// <summary>When the consistent snapshot was taken, in ISO-8601 with offset.</summary>
    public required DateTimeOffset CaptureTime { get; init; }

    /// <summary>The source-specific adapter family that produced the snapshot (e.g. wechat-windows).</summary>
    public required string CaptureAdapterFamily { get; init; }

    public required string CaptureAdapterVersion { get; init; }

    /// <summary>baseline for a full capture; incremental is reserved for future work.</summary>
    public required RawCaptureMode Mode { get; init; }

    public required RawGenerationCompleteness Completeness { get; init; }

    public int ArtifactCount { get; init; }
}

/// <summary>
/// One captured artifact as referenced by the manifest. Every artifact has a verifiable
/// SHA-256 checksum and a role describing what kind of evidence it is.
/// </summary>
public sealed record RawArtifactDescriptor
{
    /// <summary>
    /// What this artifact represents, e.g. <c>source-database</c>. Roles are source-neutral
    /// strings so the Raw Vault does not leak WeChat table names into Core/CLI.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>The original source-relative name, kept for provenance only.</summary>
    public required string Name { get; init; }

    /// <summary>Path of the artifact relative to the generation directory.</summary>
    public required string ContentRef { get; init; }

    /// <summary>Lowercase hex SHA-256 of the artifact content.</summary>
    public required string Sha256 { get; init; }

    public required long Size { get; init; }

    /// <summary>The physical format of the preserved content, e.g. <c>sqlite</c>.</summary>
    public string? SourceFormat { get; init; }

    /// <summary>True when the artifact stores decrypted content readable without the upstream key.</summary>
    public bool IsDecrypted { get; init; }

    /// <summary>Optional source-adapter-specific metadata (page counts, WAL frames, etc.).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// A diagnostic in the manifest. Manifest diagnostics are a self-contained model so the Raw
/// Vault format does not depend on the canonical archive's diagnostic types.
/// </summary>
public sealed record RawManifestDiagnostic
{
    public required string Severity { get; init; }

    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? SourceType { get; init; }

    public string? SourceSubtype { get; init; }

    public int Count { get; init; } = 1;

    public static RawManifestDiagnostic Fatal(string code, string message) =>
        new() { Severity = "fatal", Code = code, Message = message };

    public static RawManifestDiagnostic Partial(string code, string message) =>
        new() { Severity = "partial", Code = code, Message = message };

    public static RawManifestDiagnostic Info(string code, string message) =>
        new() { Severity = "info", Code = code, Message = message };
}
