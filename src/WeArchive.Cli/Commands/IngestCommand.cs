using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure;

namespace WeArchive.Cli.Commands;

/// <summary>Thin CLI transport for Raw Vault to canonical conversation ingestion.</summary>
public sealed class IngestCommand(RawVaultIngestService ingest) : ICliCommand
{
    public string Name => "ingest";
    public string Description => "Ingest captured Raw Vault evidence into the canonical archive.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string? account = null;
        string? conversation = null;
        var replay = false;
        for (var i = 0; i < args.Count; i++)
        {
            var value = args[i];
            if (value is "--account" or "--conversation")
            {
                if (++i >= args.Count || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new CliUsageException($"{value} requires a value.");
                if (value == "--account") account = args[i]; else conversation = args[i];
            }
            else if (value == "--replay") replay = true;
            else throw new CliUsageException($"unknown option '{value}' for ingest.");
        }
        if (string.IsNullOrWhiteSpace(account))
            throw new CliUsageException("ingest requires --account <account-id>.");

        int processed;
        try
        {
            processed = await ingest.IngestAsync(account, conversation, new Progress<string>(context.ReportProgress), cancellationToken, replay).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            context.WriteError(CliErrorCode.Cancelled, "ingest was cancelled; the current conversation and its checkpoint were rolled back.");
            return ExitCode.Cancelled;
        }
        catch (IncompleteCanonicalCoverageException ex)
        {
            // Direct ingest reuses the same source-neutral coverage refusal as sync --conversation:
            // a generation whose evidence is not complete cannot establish a complete canonical
            // read, and the failure document carries the coverage rollup (docs/CLI.md, Issue #51).
            context.WriteError(
                CliErrorCode.IncompleteCoverage,
                ex.Message,
                CanonicalCoverageDto.From(ex.Coverage));
            return ExitCode.Failure;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.Failure, ex.Message);
            return ExitCode.Failure;
        }

        if (context.Options.Json)
            context.Stdout.WriteLine(CliJson.Serialize(new { succeeded = true, conversations_ingested = processed }));
        else
            context.Stdout.WriteLine($"Ingested {processed} conversation(s).");
        return ExitCode.Success;
    }
}
