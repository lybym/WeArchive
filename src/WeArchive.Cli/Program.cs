using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Infrastructure;

namespace WeArchive.Cli;

/// <summary>
/// Console entry point. Builds the composition root, routes the command, and maps
/// outcomes to process exit codes.
/// <para>
/// All presentation logic is in <see cref="CliHost"/>; this file only wires real
/// <c>Console</c> streams and the production service provider.
/// </para>
/// </summary>
internal static class Program
{
    /// <summary>Per-user application data directory. Never contains WeChat data caches.</summary>
    internal static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeArchive");

    internal static string ArchivePath { get; } = Path.Combine(DataDirectory, "archive", "wearchive.db");

    private static async Task<int> Main(string[] args)
    {
        // Build the production service provider: archive, exporter, application services
        // and the Windows WeChat source adapter. The CLI does not add a second composition
        // model — it reuses the same Infrastructure extensions as the transitional WPF host.
        var services = new ServiceCollection();
        services.AddWeArchiveCore(ArchivePath);
        services.AddWeChatWindowsSource();
        await using var provider = services.BuildServiceProvider();

        var exitCode = await CliHost.RunAsync(
            args,
            Console.Out,
            Console.Error,
            provider,
            GetCancellationToken());

        // Flush before the process exits so --json output is not truncated.
        Console.Out.Flush();
        Console.Error.Flush();
        return exitCode;
    }

    /// <summary>
    /// A cancellation token that triggers on Ctrl+C. When cancelled, the operation
    /// surfaces as exit 130 (docs/PRD.md FR-22) without inventing recovery semantics.
    /// </summary>
    private static CancellationToken GetCancellationToken()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        return cts.Token;
    }
}
