namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Locally-resolved default paths for CLI commands that write a derived dataset. These are
/// presentation/host concerns: the archive path and export packaging rules stay in
/// Infrastructure. The composition root supplies the values so commands remain testable
/// without touching <see cref="Console"/> or the real per-user data directory.
/// </summary>
public sealed record CliExportDefaults
{
    /// <summary>
    /// Default directory for <c>wearchive export</c> when <c>--output</c> is omitted. Lives
    /// under the per-user application data directory alongside the SQLite archive; it never
    /// holds WeChat source caches.
    /// </summary>
    public required string DefaultOutputDirectory { get; init; }
}
