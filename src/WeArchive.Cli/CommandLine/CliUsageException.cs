namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Thrown for usage or configuration validation failures that should map to exit 2
/// (docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1).
/// </summary>
public sealed class CliUsageException(string message) : Exception(message)
{
}
