using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive account list</c>: enumerates locally available source profiles (logged-in
/// accounts) with their stable account identifiers (docs/PRD.md FR-01, FR-22).
/// <para>
/// This is a thin adapter over <see cref="SourceCatalogService"/>: it contains no source-format
/// logic. An empty list is a valid result (exit 0) when the source is available; a failure to
/// reach the source is a structured <c>source_unavailable</c> diagnostic (exit 1).
/// </para>
/// </summary>
public sealed class AccountCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;

    public AccountCommand(SourceCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Name => "account";

    public string Description => "List locally available source profiles (accounts).";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0 ||
            !string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteError(CliErrorCode.UsageError, "expected 'account list'.");
            return ExitCode.UsageError;
        }

        if (args.Count > 1)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{args[1]}' for 'account list'.");
            return ExitCode.UsageError;
        }

        context.ReportProgress("Listing accounts…");

        IReadOnlyList<SourceAccount> accounts;
        try
        {
            accounts = await _catalog
                .ListAccountsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The source could not be reached (not running, no profile, key acquisition failed,
            // etc.). doctor reports this as a result; the discovery list commands treat it as a
            // runtime failure with a stable code so automation can branch on it.
            context.WriteError(CliErrorCode.SourceUnavailable, ex.Message);
            return ExitCode.Failure;
        }

        var items = accounts
            .Select(account => new AccountItemDto
            {
                SourceProfileId = account.SourceProfileId,
                StableId = StableIds.Account(_catalog.AdapterName, account.SourceProfileId),
                DisplayName = account.DisplayName,
                IsCurrent = account.IsCurrent,
                LastActiveAt = account.LastActiveAt,
                DataRootPath = account.DataRootPath,
            })
            .ToList();

        WriteResult(context, items);
        return ExitCode.Success;
    }

    private static void WriteResult(CliContext context, IReadOnlyList<AccountItemDto> items)
    {
        if (context.Options.Json)
        {
            // A JSON array is a single document (gh-style). Empty list -> "[]".
            context.Stdout.WriteLine(CliJson.Serialize(items));
            return;
        }

        if (items.Count == 0)
        {
            context.Stdout.WriteLine("No source profiles found.");
            return;
        }

        context.Stdout.WriteLine($"Accounts ({items.Count}):");
        foreach (var item in items)
        {
            var marker = item.IsCurrent ? "*" : " ";
            var name = string.IsNullOrEmpty(item.DisplayName) ? "" : $"  {item.DisplayName}";
            context.Stdout.WriteLine($"  {marker} {item.SourceProfileId,-24} {item.StableId}{name}");
        }
    }
}
