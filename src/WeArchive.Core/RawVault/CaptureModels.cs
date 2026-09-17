namespace WeArchive.Core.RawVault;

/// <summary>
/// Capture mode. Only <see cref="Baseline"/> (full capture) is implemented in M1.5;
/// <see cref="Incremental"/> is reserved for future work and is explicitly a non-goal of
/// Issue #22.
/// </summary>
public enum RawCaptureMode
{
    /// <summary>Capture the entire supported source snapshot into one generation.</summary>
    Baseline,

    /// <summary>Reserved for future incremental capture; not implemented.</summary>
    Incremental,
}

/// <summary>
/// Completeness verdict for a published generation. A generation is only published as
/// <see cref="Complete"/> when every required artifact validates; a Fatal coverage or
/// consistency failure never publishes a generation as complete (Issue #22).
/// </summary>
public enum RawGenerationCompleteness
{
    /// <summary>All required artifacts captured and verified.</summary>
    Complete,

    /// <summary>Optional evidence missing but required evidence present; still a valid generation.</summary>
    Partial,

    /// <summary>Required evidence missing or inconsistent; must not be published as complete.</summary>
    Incomplete,
}

/// <summary>One published Raw Vault generation, reopened for read-only inspection.</summary>
public sealed record RawGeneration
{
    public required string GenerationId { get; init; }

    public required string AccountId { get; init; }

    public required RawManifest Manifest { get; init; }

    /// <summary>Absolute directory holding <c>manifest.json</c> and <c>artifacts/</c>.</summary>
    public required string GenerationDirectory { get; init; }
}

/// <summary>A compact summary used for generation discovery without opening every manifest.</summary>
public sealed record RawGenerationSummary
{
    public required string GenerationId { get; init; }

    public required string AccountId { get; init; }

    public required DateTimeOffset CaptureTime { get; init; }

    public required RawGenerationCompleteness Completeness { get; init; }

    public required int ArtifactCount { get; init; }

    public string? PreviousGenerationId { get; init; }
}

/// <summary>Context the store needs to begin staging a new generation.</summary>
public sealed record RawGenerationContext
{
    public required string AccountId { get; init; }

    public required string SourceProfileId { get; init; }

    public required DateTimeOffset CaptureTime { get; init; }

    public required string CaptureAdapterFamily { get; init; }

    public required string CaptureAdapterVersion { get; init; }
}

/// <summary>Request for <c>CaptureService.CaptureAccountAsync</c>.</summary>
public sealed record CaptureRequest
{
    /// <summary>
    /// The source profile id to capture. When null, the current account is auto-selected
    /// (never prompts, safe under <c>--no-input</c>).
    /// </summary>
    public string? SourceProfileId { get; init; }
}

/// <summary>Outcome of a capture attempt.</summary>
public sealed record CaptureResult
{
    public required bool Succeeded { get; init; }

    public required string GenerationId { get; init; }

    public required string AccountId { get; init; }

    public required string SourceProfileId { get; init; }

    public required DateTimeOffset CaptureTime { get; init; }

    public required RawGenerationCompleteness Completeness { get; init; }

    public required int ArtifactCount { get; init; }

    public IReadOnlyList<RawManifestDiagnostic> Diagnostics { get; init; } = [];

    public string? PreviousGenerationId { get; init; }

    /// <summary>Present only when <see cref="Succeeded"/> is false.</summary>
    public string? FailureMessage { get; init; }

    public static CaptureResult Failed(
        string generationId,
        string accountId,
        string sourceProfileId,
        DateTimeOffset captureTime,
        string message,
        IReadOnlyList<RawManifestDiagnostic>? diagnostics = null) => new()
    {
        Succeeded = false,
        GenerationId = generationId,
        AccountId = accountId,
        SourceProfileId = sourceProfileId,
        CaptureTime = captureTime,
        Completeness = RawGenerationCompleteness.Incomplete,
        ArtifactCount = 0,
        Diagnostics = diagnostics ?? [],
        FailureMessage = message,
    };
}

/// <summary>Progress reported by the capture adapter during a capture run.</summary>
public sealed record CaptureProgress
{
    public required string Stage { get; init; }

    public int Processed { get; init; }

    public int Total { get; init; }

    public string? Detail { get; init; }
}
