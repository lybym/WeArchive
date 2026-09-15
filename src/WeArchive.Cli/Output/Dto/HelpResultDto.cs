using System.Text.Json.Serialization;

namespace WeArchive.Cli.Output.Dto;

/// <summary>
/// JSON result of <c>wearchive --help --json</c> and of a bare <c>wearchive --json</c> probe.
/// Machine callers can discover the command surface without parsing prose.
/// <para>
/// Fields are pinned by <c>[JsonPropertyName]</c> so a C# rename cannot silently change
/// the wire contract (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1).
/// </para>
/// </summary>
public sealed record HelpResultDto
{
    [JsonPropertyName("usage")]
    public required string Usage { get; init; }

    [JsonPropertyName("commands")]
    public required IReadOnlyList<HelpCommandDto> Commands { get; init; }

    [JsonPropertyName("options")]
    public required IReadOnlyList<HelpOptionDto> Options { get; init; }
}

public sealed record HelpCommandDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }
}

public sealed record HelpOptionDto
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }
}
