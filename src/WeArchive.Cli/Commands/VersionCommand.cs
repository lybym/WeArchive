using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive version</c>: prints the product version.
/// <para>
/// This is the simplest command and the reference implementation of the CLI JSON DTO
/// convention (docs/PRD.md FR-22, docs/adr/0006-cli-first-product-surface.md).
/// </para>
/// </summary>
public sealed class VersionCommand : ICliCommand
{
    private const string Framework = "net10.0-windows";
    private const string Platform = "win-x64";

    public string Name => "version";

    public string Description => "Print the WeArchive version.";

    public Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var dto = new VersionResultDto
        {
            Version = ProductVersion.Current,
            Framework = Framework,
            Platform = Platform,
        };

        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(dto));
        }
        else
        {
            context.Stdout.WriteLine($"WeArchive {dto.Version}");
            context.Stdout.WriteLine($"  framework: {dto.Framework}");
            context.Stdout.WriteLine($"  platform:  {dto.Platform}");
        }

        return Task.FromResult(ExitCode.Success);
    }
}
