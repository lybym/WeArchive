using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive capture [--account &lt;id&gt;]</c>: captures a supported local WeChat account
/// into a durable, versioned, immutable Raw Vault generation that remains readable without the
/// original WeChat database key (docs/PRD.md FR-04/FR-05/FR-06/FR-13/FR-20, Issue #22 / M1.5).
/// <para>
/// This is a thin transport adapter over <see cref="CaptureService"/>. It resolves the
/// requested account through the source catalog, delegates the entire snapshot -&gt; publish
/// operation to <see cref="CaptureService"/>, and maps the outcome to a result or an error
/// document. It contains no source-format, key-acquisition or manifest-publication logic: a
/// Fatal source/coverage failure means the generation was discarded and the CLI surfaces it
/// as exit 1 (R1, no R3+ recovery machinery).
/// </para>
/// </summary>
public sealed class CaptureCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;
    private readonly CaptureService _capture;

    public CaptureCommand(SourceCatalogService catalog, CaptureService capture)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public string Name => "capture";

    public string Description => "Capture an account into a Raw Vault generation.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var requestedProfileId = ParseArgs(args);

        CliReporting.Progress(context, "Describing source…");
        SourceDescriptor descriptor;
        try
        {
            descriptor = await _catalog.DescribeSourceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.SourceUnavailable, ex.Message);
            return ExitCode.Failure;
        }

        if (!descriptor.IsAvailable)
        {
            context.WriteError(CliErrorCode.SourceUnavailable,
                descriptor.UnavailableReason ?? "The source is not available.");
            return ExitCode.Failure;
        }

        CliReporting.Progress(context, "Resolving account…");
        IReadOnlyList<SourceAccount> accounts;
        try
        {
            accounts = await _catalog.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.SourceUnavailable, ex.Message);
            return ExitCode.Failure;
        }

        if (accounts.Count == 0)
        {
            context.WriteError(CliErrorCode.NoAccounts,
                "no source profiles are available; cannot resolve an account to capture.");
            return ExitCode.Failure;
        }

        var account = ResolveAccount(accounts, requestedProfileId);
        if (account is null)
        {
            context.WriteError(CliErrorCode.AccountNotFound,
                $"account '{requestedProfileId}' was not found.");
            return ExitCode.Failure;
        }

        var request = new CaptureRequest { SourceProfileId = account.SourceProfileId };

        var progress = new CliProgress<CaptureProgress>(context, FormatProgress);
        var outcome = await _capture
            .CaptureAccountAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            var message = outcome.FailureMessage ?? "Capture did not complete.";
            context.WriteError(CliErrorCode.Failure, message);
            return ExitCode.Failure;
        }

        var result = new CaptureResultDto
        {
            GenerationId = outcome.GenerationId,
            AccountId = outcome.AccountId,
            SourceProfileId = outcome.SourceProfileId,
            CaptureTime = FormatTimestamp(outcome.CaptureTime),
            Completeness = outcome.Completeness.ToString().ToLowerInvariant(),
            Mode = "baseline",
            CaptureAdapterFamily = _capture.CaptureAdapterFamily,
            CaptureAdapterVersion = _capture.CaptureAdapterVersion,
            ArtifactCount = outcome.ArtifactCount,
            PreviousGenerationId = outcome.PreviousGenerationId,
            Diagnostics = [.. outcome.Diagnostics.Select(CaptureResultDto.From)],
        };

        WriteResult(context, result);
        return ExitCode.Success;
    }

    private static string? ParseArgs(IReadOnlyList<string> args)
    {
        string? account = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--account":
                    if (i + 1 >= args.Count)
                        throw new CliUsageException("--account requires a value.");
                    account = args[++i];
                    break;
                default:
                    throw new CliUsageException($"unknown option '{args[i]}' for capture.");
            }
        }

        return account;
    }

    private static SourceAccount? ResolveAccount(
        IReadOnlyList<SourceAccount> accounts,
        string? requestedProfileId)
    {
        if (string.IsNullOrWhiteSpace(requestedProfileId))
        {
            return accounts.FirstOrDefault(a => a.IsCurrent) ?? accounts[0];
        }

        return accounts.FirstOrDefault(a =>
            string.Equals(a.SourceProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteResult(CliContext context, CaptureResultDto result)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(result));
            return;
        }

        context.Stdout.WriteLine($"Captured account {result.AccountId}");
        context.Stdout.WriteLine($"  generation:   {result.GenerationId}");
        context.Stdout.WriteLine($"  capture time: {result.CaptureTime}");
        context.Stdout.WriteLine($"  completeness: {result.Completeness}");
        context.Stdout.WriteLine($"  artifacts:    {result.ArtifactCount}");
        if (result.PreviousGenerationId is not null)
        {
            context.Stdout.WriteLine($"  previous:     {result.PreviousGenerationId}");
        }

        if (result.Diagnostics.Count > 0)
        {
            context.Stdout.WriteLine("  diagnostics:");
            foreach (var d in result.Diagnostics)
            {
                context.Stdout.WriteLine($"    {d.Severity} {d.Code}: {d.Message}");
            }
        }
    }

    private static string FormatProgress(CaptureProgress p) =>
        p.Total > 0
            ? $"{p.Stage}: {p.Processed}/{p.Total}"
            : string.IsNullOrEmpty(p.Stage) ? string.Empty : p.Stage;

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
