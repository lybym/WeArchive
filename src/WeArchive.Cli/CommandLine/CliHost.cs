using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;

namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Top-level CLI entry point logic, separated from <c>Program.Main</c> so it is testable
/// with in-memory <see cref="TextWriter"/> writers instead of <c>Console</c>.
/// <para>
/// Every path — help, usage error, cancellation or runtime failure — honours the
/// <c>--json</c> contract: stdout receives exactly one JSON document and human
/// diagnostics stay on stderr. docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1.
/// </para>
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

        // Seeded with the default options so the failure handlers below still know whether
        // the caller asked for machine output even if parsing itself failed.
        var options = GlobalOptions.Default;

        try
        {
            IReadOnlyList<string> remaining;
            (options, remaining) = CommandLineParser.Parse(args);

            if (options.ShowVersion)
            {
                WriteVersion(stdout, options);
                return ExitCode.Success;
            }

            var router = new CommandRouter(services);

            if (options.ShowHelp || remaining.Count == 0)
            {
                router.WriteHelp(stdout, options);
                return ExitCode.Success;
            }

            var context = new CliContext(stdout, stderr, options);
            return await router.ExecuteAsync(remaining, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            CliErrors.Write(stdout, stderr, options, CliErrorCode.UsageError, ex.Message);
            return ExitCode.UsageError;
        }
        catch (OperationCanceledException)
        {
            CliErrors.Write(stdout, stderr, options, CliErrorCode.Cancelled, "operation cancelled.");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            CliErrors.Write(stdout, stderr, options, CliErrorCode.Failure, ex.Message);
            return ExitCode.Failure;
        }
    }

    private static void WriteVersion(TextWriter stdout, GlobalOptions options)
    {
        var dto = new VersionResultDto
        {
            Version = ProductVersion.Current,
            Framework = "net10.0-windows",
            Platform = "win-x64",
        };

        if (options.Json)
        {
            stdout.WriteLine(CliJson.Serialize(dto));
        }
        else
        {
            stdout.WriteLine($"WeArchive {dto.Version}");
            stdout.WriteLine($"  framework: {dto.Framework}");
            stdout.WriteLine($"  platform:  {dto.Platform}");
        }
    }
}
