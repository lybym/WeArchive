using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Core.Domain;
using WeArchive.Infrastructure;

namespace WeArchive.Cli.Commands;

/// <summary>Thin CLI transport for Raw-Vault-only canonical rebuild.</summary>
public sealed class RebuildCommand(RebuildService rebuild) : ICliCommand
{
    public string Name => "rebuild";
    public string Description => "Rebuild the canonical archive from Raw Vault evidence.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 0)
            throw new CliUsageException($"unknown option '{args[0]}' for rebuild.");

        var progress = new Progress<string>(context.ReportProgress);
        ArchiveStats stats;
        try
        {
            stats = await rebuild.RebuildAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            context.WriteError(CliErrorCode.Cancelled, "rebuild was cancelled; the selected archive was not replaced.");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.Failure, ex.Message);
            return ExitCode.Failure;
        }

        var result = new
        {
            succeeded = true,
            archive_path = stats.ArchivePath,
            account_count = stats.AccountCount,
            participant_count = stats.ParticipantCount,
            conversation_count = stats.ConversationCount,
            message_count = stats.MessageCount,
        };
        if (context.Options.Json)
            context.Stdout.WriteLine(CliJson.Serialize(result));
        else
            context.Stdout.WriteLine($"Rebuilt archive: {stats.AccountCount} account(s), {stats.ConversationCount} conversation(s), {stats.MessageCount} message(s).");
        return ExitCode.Success;
    }
}
