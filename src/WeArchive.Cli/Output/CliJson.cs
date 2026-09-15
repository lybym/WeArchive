using System.Text.Json;

namespace WeArchive.Cli.Output;

/// <summary>
/// Stable JSON serialization options for CLI machine output.
/// <para>
/// CLI JSON DTOs are presentation contracts (docs/ARCHITECTURE.md section 3.1.1).
/// Field names are explicit <c>[JsonPropertyName]</c> attributes so that C# property
/// renames never silently change the wire contract. The options are shared so every
/// command emits the same encoding.
/// </para>
/// </summary>
public static class CliJson
{
    /// <summary>
    /// Compact, machine-oriented options: no indentation, snake_case fallback naming,
    /// no ANSI. Each command writes exactly one serialized document to stdout.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Serializes <paramref name="value"/> to a single JSON string suitable for stdout.
    /// </summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
