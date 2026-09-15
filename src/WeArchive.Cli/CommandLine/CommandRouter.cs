using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Core.Abstractions;

namespace WeArchive.Cli.CommandLine;

/// <summary>
/// Routes the first positional argument to the matching <see cref="ICliCommand"/>.
/// Commands are created lazily from the <see cref="IServiceProvider"/> so that a command
/// which depends on the WeChat adapter is never constructed unless it is actually invoked.
/// </summary>
public sealed class CommandRouter
{
    private readonly IServiceProvider _services;
    private readonly Dictionary<string, Func<IServiceProvider, ICliCommand>> _factories;

    public CommandRouter(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _factories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["version"] = _ => new Commands.VersionCommand(),
            ["doctor"] = sp => new Commands.DoctorCommand(
                sp.GetRequiredService<ISourceAdapter>(),
                sp.GetRequiredService<IArchiveStore>()),
        };
    }

    /// <summary>Command names in registration order (for help).</summary>
    public IReadOnlyList<string> CommandNames => [.. _factories.Keys];

    public async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        CliContext context,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            WriteHelp(context.Stdout, context);
            return ExitCode.Success;
        }

        var name = args[0];
        if (!_factories.TryGetValue(name, out var factory))
        {
            context.Stderr.WriteLine($"error: unknown command '{name}'");
            WriteHelp(context.Stderr, context);
            return ExitCode.UsageError;
        }

        var command = factory(_services);
        var commandArgs = args.Skip(1).ToList();
        return await command.ExecuteAsync(context, commandArgs, cancellationToken)
            .ConfigureAwait(false);
    }

    [SuppressMessage("Usage", "CA1841:Favor Dictionary methods over calling ContainsKey")]
    internal void WriteHelp(TextWriter writer, CliContext context)
    {
        writer.WriteLine("Usage: wearchive <command> [options]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var name in _factories.Keys)
        {
            var command = _factories[name](_services);
            writer.WriteLine($"  {name,-12} {command.Description}");
        }
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --json        Emit exactly one JSON document on stdout");
        writer.WriteLine("  --quiet       Suppress non-essential progress on stderr");
        writer.WriteLine("  --no-input    Never prompt; fail when required input is missing");
        writer.WriteLine("  --version     Print the product version and exit");
        writer.WriteLine("  --help, -h    Show this help and exit");
    }
}
