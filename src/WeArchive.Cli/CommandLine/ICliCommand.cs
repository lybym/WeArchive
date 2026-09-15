namespace WeArchive.Cli.CommandLine;

/// <summary>
/// A single CLI command. Commands are thin adapters over application services:
/// they parse their own arguments, call a service, and write a result to
/// <see cref="CliContext.Stdout"/> (JSON or human) and progress to
/// <see cref="CliContext.Stderr"/>, then return a documented exit code.
/// </summary>
public interface ICliCommand
{
    /// <summary>Command name as typed by the user, e.g. <c>doctor</c>.</summary>
    string Name { get; }

    /// <summary>One-line description shown in help.</summary>
    string Description { get; }

    /// <summary>
    /// Executes the command.
    /// </summary>
    /// <param name="context">stdout/stderr writers and global options.</param>
    /// <param name="args">Arguments after the command name (command-specific).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A documented exit code (<see cref="ExitCode"/>).</returns>
    Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken);
}
