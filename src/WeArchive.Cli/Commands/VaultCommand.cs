using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Infrastructure.RawVault;

namespace WeArchive.Cli.Commands;

/// <summary>Thin source-independent transport for read-only retained-vault inspection.</summary>
public sealed class VaultCommand(VaultInspectionService inspection) : ICliCommand
{
    public string Name => "vault";
    public string Description => "Account for or verify retained Raw Vault evidence (stats | verify).";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (options, positional, error) = CommandOptionParser.Parse(args, new HashSet<string>(StringComparer.Ordinal) { "--vault-root" });
        if (error is not null || positional.Count != 1 || positional[0] is not ("stats" or "verify"))
            throw new CliUsageException(error ?? "vault requires stats or verify [--vault-root <path>].");
        try
        {
            var result = await inspection.InspectAsync(positional[0] == "verify", options.GetValueOrDefault("--vault-root"), cancellationToken).ConfigureAwait(false);
            if (context.Options.Json)
                context.Stdout.WriteLine(CliJson.Serialize(new
                {
                    schema_version = 1, succeeded = result.Succeeded, operation = result.Operation,
                    error = result.Succeeded ? null : new { code = CliErrorCode.Failure, message = result.Failures[0].Message },
                    metrics = result.Metrics?.ToDictionary(pair => pair.Key,
                        pair => new { value = pair.Value.Value, unit = pair.Value.Unit, basis = pair.Value.Basis }),
                    accounts = result.Accounts.Select(a => new { account_id = a.AccountId, derived_index_status = a.DerivedIndexStatus }),
                    failures = result.Failures.Select(f => new { account_id = f.AccountId, generation_id = f.GenerationId, artifact = f.Artifact, message = f.Message }),
                }));
            else if (result.Succeeded)
            {
                context.Stdout.WriteLine($"Raw Vault {result.Operation}: integrity verified.");
                foreach (var metric in result.Metrics!)
                    context.Stdout.WriteLine($"{metric.Key}: {metric.Value.Value?.ToString() ?? "unavailable"} {metric.Value.Unit} ({metric.Value.Basis})");
                foreach (var account in result.Accounts)
                    context.Stdout.WriteLine($"{account.AccountId}: derived index {account.DerivedIndexStatus}");
            }
            foreach (var failure in result.Failures)
                context.Stderr.WriteLine($"Raw Vault failure: {failure.AccountId}/{failure.GenerationId}/{failure.Artifact}: {failure.Message}");
            return result.Succeeded ? ExitCode.Success : ExitCode.Failure;
        }
        catch (OperationCanceledException)
        {
            context.WriteError(CliErrorCode.Cancelled, "vault inspection was cancelled; no evidence was changed.");
            return ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.Failure, ex.Message);
            return ExitCode.Failure;
        }
    }
}
