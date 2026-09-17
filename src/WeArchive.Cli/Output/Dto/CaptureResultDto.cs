using System.Text.Json.Serialization;
using WeArchive.Core.RawVault;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive capture</c>. Reports the published generation's identity,
/// completeness and diagnostics without re-deriving any capture semantics
/// (docs/PRD.md FR-04/FR-13/FR-20, docs/RAW_VAULT.md, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
public sealed record CaptureResultDto
{
    [JsonPropertyName("generation_id")]
    public required string GenerationId { get; init; }

    [JsonPropertyName("account_id")]
    public required string AccountId { get; init; }

    [JsonPropertyName("source_profile_id")]
    public required string SourceProfileId { get; init; }

    [JsonPropertyName("capture_time")]
    public required string CaptureTime { get; init; }

    [JsonPropertyName("completeness")]
    public required string Completeness { get; init; }

    [JsonPropertyName("mode")]
    public required string Mode { get; init; }

    [JsonPropertyName("capture_adapter_family")]
    public required string CaptureAdapterFamily { get; init; }

    [JsonPropertyName("capture_adapter_version")]
    public required string CaptureAdapterVersion { get; init; }

    [JsonPropertyName("artifact_count")]
    public int ArtifactCount { get; init; }

    [JsonPropertyName("previous_generation_id")]
    public string? PreviousGenerationId { get; init; }

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<CaptureDiagnosticDto> Diagnostics { get; init; } = [];

    public static CaptureDiagnosticDto From(RawManifestDiagnostic diagnostic) => new()
    {
        Severity = diagnostic.Severity,
        Code = diagnostic.Code,
        Message = diagnostic.Message,
        Count = diagnostic.Count,
        SourceType = diagnostic.SourceType,
        SourceSubtype = diagnostic.SourceSubtype,
    };
}

/// <summary>
/// CLI presentation of one Raw Vault manifest diagnostic. Diagnostics never carry chat
/// content or database keys: only severity, stable code, a short engineering message, an
/// aggregate count and upstream type codes for provenance.
/// </summary>
public sealed record CaptureDiagnosticDto
{
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("source_type")]
    public string? SourceType { get; init; }

    [JsonPropertyName("source_subtype")]
    public string? SourceSubtype { get; init; }
}
