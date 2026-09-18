using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Services;

namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Routes the first positional argument to the matching <see cref="ICliCommand"/>.
/// Commands are created lazily from the <see cref="IServiceProvider"/> so that a command
/// which depends on the WeChat adapter is never constructed unless it is actually invoked.
/// </summary>
public sealed class CommandRouter
{
    /// <summary>Usage line shared by the human and the machine-readable help renderings.</summary>
    public const string Usage = "wearchive <command> [options]";

    /// <summary>
    /// Global options documented by help. Declared once so the human help text and the
    /// JSON help document cannot drift apart.
    /// </summary>
    private static readonly IReadOnlyList<HelpOptionDto> DocumentedOptions =
    [
        new() { Name = "--json", Description = "Emit exactly one JSON document on stdout" },
        new() { Name = "--quiet", Description = "Suppress non-essential progress on stderr" },
        new() { Name = "--no-input", Description = "Never prompt; fail when required input is missing" },
        new() { Name = "--version", Description = "Print the product version and exit" },
        new() { Name = "--help, -h", Description = "Show this help and exit" },
    ];

    private readonly IServiceProvider _services;
    private readonly Dictionary<string, CommandEntry> _factories;

    /// <summary>
    /// A command's lazy constructor plus its one-line description. The description is stored
    /// separately so rendering help never constructs commands (some depend on the source
    /// adapter or the archive, which a help-only caller has no reason to build).
    /// </summary>
    private sealed record CommandEntry(Func<IServiceProvider, ICliCommand> Factory, string Description);

    public CommandRouter(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _factories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["version"] = new(_ => new Commands.VersionCommand(), "Print the WeArchive version."),
            ["doctor"] = new(sp => new Commands.DoctorCommand(
                sp.GetRequiredService<ISourceAdapter>(),
                sp.GetRequiredService<IArchiveStore>()),
                "Report source and archive readiness."),
            ["account"] = new(sp => new Commands.AccountCommand(
                sp.GetRequiredService<SourceCatalogService>()),
                "List locally available source profiles (accounts)."),
            ["conversation"] = new(sp => new Commands.ConversationCommand(
                sp.GetRequiredService<SourceCatalogService>()),
                "List conversations or show one (list | show <id-or-alias>)."),
            ["sync"] = new(sp => new Commands.SyncCommand(
                sp.GetRequiredService<SourceCatalogService>(),
                sp.GetRequiredService<ImportService>()),
                "Import one conversation into the archive."),
            ["export"] = new(sp => new Commands.ExportCommand(
                sp.GetRequiredService<SourceCatalogService>(),
                sp.GetRequiredService<ArchiveWorkflow>(),
                sp.GetRequiredService<CliExportDefaults>()),
                "Export one conversation to a JSONL dataset."),
            ["capture"] = new(sp => new Commands.CaptureCommand(
                sp.GetRequiredService<SourceCatalogService>(),
                sp.GetRequiredService<CaptureService>()),
                "Capture an account into a Raw Vault generation."),
        };
    }

    /// <summary>Command names in registration order (for help).</summary>
    public IReadOnlyList<string> CommandNames => [.. _factories.Keys];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        CliContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        if (args.Count == 0)
        {
            WriteHelp(context.Stdout, context.Options);
            return ExitCode.Success;
        }

        var name = args[0];
        if (!_factories.TryGetValue(name, out var entry))
        {
            // A machine caller gets the error envelope on stdout; the human explanation
            // (including the help text) stays on stderr.
            context.WriteError(CliErrorCode.UsageError, $"unknown command '{name}'");
            WriteHumanHelp(context.Stderr);
            return ExitCode.UsageError;
        }

        var command = entry.Factory(_services);
        var commandArgs = args.Skip(1).ToList();
        return await command.ExecuteAsync(context, commandArgs, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the machine-readable help document. Stable field names are pinned by
    /// <c>[JsonPropertyName]</c> (docs/PRD.md FR-22).
    /// </summary>
    public HelpResultDto DescribeHelp()
    {
        var commands = new List<HelpCommandDto>(_factories.Count);
        foreach (var entry in _factories)
        {
            commands.Add(new HelpCommandDto
            {
                Name = entry.Key,
                Description = entry.Value.Description,
            });
        }

        return new HelpResultDto
        {
            Usage = Usage,
            Commands = commands,
            Options = DocumentedOptions,
        };
    }

    /// <summary>
    /// Writes help in the mode requested by <paramref name="options"/>: exactly one JSON
    /// document when <c>--json</c> is set, human-readable prose otherwise.
    /// docs/ARCHITECTURE.md section 3.1.1.
    /// </summary>
    public void WriteHelp(TextWriter writer, GlobalOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Json)
        {
            writer.WriteLine(CliJson.Serialize(DescribeHelp()));
            return;
        }

        WriteHumanHelp(writer);
    }

    /// <summary>
    /// Writes human-readable help. Used directly by diagnostic paths that must keep
    /// stderr human-readable even in <c>--json</c> mode.
    /// </summary>
    public void WriteHumanHelp(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine($"Usage: {Usage}");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var entry in _factories)
        {
            writer.WriteLine($"  {entry.Key,-12} {entry.Value.Description}");
        }

        writer.WriteLine();
        writer.WriteLine("Options:");
        foreach (var option in DocumentedOptions)
        {
            writer.WriteLine($"  {option.Name,-14}{option.Description}");
        }
    }
}
