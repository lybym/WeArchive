using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive version</c> / <c>wearchive --version</c>.
/// Stable field names are pinned by <c>[JsonPropertyName]</c>.
/// </summary>
public sealed record VersionResultDto
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("framework")]
    public required string Framework { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }
}
