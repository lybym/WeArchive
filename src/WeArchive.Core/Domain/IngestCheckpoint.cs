namespace WeArchive.Core.Domain;

/// <summary>Opaque, versioned Raw Vault ingestion progress for one logical scope.</summary>
public sealed record IngestCheckpoint
{
    public required string Id { get; init; }
    public required string AccountId { get; init; }
    public required string AdapterFamily { get; init; }
    public required string ScopeKind { get; init; }
    public required string ScopeId { get; init; }
    public required string CheckpointJson { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
