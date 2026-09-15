using WeArchive.Cli.Commands;
using WeArchive.Cli.Output.Dto;

namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Top-level CLI entry point logic, separated from <c>Program.Main</c> so it is testable
/// with in-memory <see cref="TextWriter"/> writers instead of <c>Console</c>.
/// </summary>
public static class CliHost
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            var (options, remaining) = CommandLineParser.Parse(args);

            if (options.ShowVersion)
            {
                WriteVersion(stdout, options);
                return ExitCode.Success;
            }

            if (options.ShowHelp || remaining.Count == 0)
            {
                WriteHelp(stdout, services);
                return ExitCode.Success;
            }

            var router = new CommandRouter(services);
            var context = new CliContext(stdout, stderr, options);
            return await router.ExecuteAsync(remaining, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return ExitCode.UsageError;
        }
        catch (OperationCanceledException)
        {
            stderr.WriteLine("error: operation cancelled.");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return ExitCode.Failure;
        }
    }

    private static void WriteVersion(TextWriter stdout, GlobalOptions options)
    {
        var dto = new VersionResultDto
        {
            Version = GetVersionString(),
            Framework = "net10.0-windows",
            Platform = "win-x64",
        };

        if (options.Json)
        {
            stdout.WriteLine(Output.CliJson.Serialize(dto));
        }
        else
        {
            stdout.WriteLine($"WeArchive {dto.Version}");
            stdout.WriteLine($"  framework: {dto.Framework}");
            stdout.WriteLine($"  platform:  {dto.Platform}");
        }
    }

    private static void WriteHelp(TextWriter stdout, IServiceProvider services)
    {
        var router = new CommandRouter(services);
        router.WriteHelp(stdout, new CliContext(stdout, TextWriter.Null, GlobalOptions.Default));
    }

    private static string GetVersionString()
    {
        var version = typeof(CliHost).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
