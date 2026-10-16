using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive status</c>: one read-only report of archive freshness across the three independent
/// stages — capture, ingest and canonical (issue #107, docs/CLI.md, docs/HARNESS.md section 10).
/// <para>
/// This is a thin transport over <see cref="ArchiveQueryService.GetFreshnessAsync"/>: the service
/// owns the three independent reads and their degradation semantics, and this command only renders
/// them. It depends on no live source adapter, opens no Raw Vault artifact and never writes the
/// archive. Reporting that a stage is unavailable is a successful observation, not a failure; only
/// an unreadable canonical archive fails the command, because the report itself could not be read.
/// </para>
/// </summary>
public sealed class StatusCommand : ICliCommand
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal);

    private readonly ArchiveQueryService _query;

    public StatusCommand(ArchiveQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public string Name => "status";

    public string Description =>
        "Report read-only archive freshness across capture, ingest and canonical stages.";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        var (options, positional, error) = CommandOptionParser.Parse(args, KnownOptions);
        if (error is not null)
        {
            context.WriteError(CliErrorCode.UsageError, error);
            return ExitCode.UsageError;
        }

        if (options.Count > 0 || positional.Count > 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "unexpected argument for 'status'; the report takes no options.");
            return ExitCode.UsageError;
        }

        context.ReportProgress("Reading archive freshness…");

        ArchiveFreshness freshness;
        try
        {
            freshness = await _query.GetFreshnessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveQueryException ex)
        {
            return QueryCommandSupport.ReportQueryFailure(context, ex);
        }

        WriteResult(context, freshness);
        return ExitCode.Success;
    }

    private static void WriteResult(CliContext context, ArchiveFreshness freshness)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(StatusResultDto.From(freshness)));
            return;
        }

        var canonical = freshness.Canonical;
        context.Stdout.WriteLine(
            $"Archive status (as of {QueryCommandSupport.FormatTimestamp(canonical.QueriedAt)}, basis: {canonical.ArchivePath}):");
        context.Stdout.WriteLine(
            $"  canonical: {canonical.AccountCount} account(s), {canonical.ConversationCount} conversation(s), " +
            $"{canonical.ParticipantCount} participant(s), {canonical.MessageCount} message(s), " +
            $"last message {(canonical.LastMessageAt is null ? "(none)" : QueryCommandSupport.FormatTimestamp(canonical.LastMessageAt.Value))}");

        if (freshness.CaptureUnavailableReason is not null)
        {
            context.Stdout.WriteLine($"  capture: unavailable ({freshness.CaptureUnavailableReason})");
        }
        else
        {
            context.Stdout.WriteLine($"  capture: {freshness.Capture.Count} account(s)");
            foreach (var capture in freshness.Capture)
            {
                var generation = capture.GenerationId is null
                    ? capture.UnavailableReason is null
                        ? "no published generation"
                        : $"unknown ({capture.UnavailableReason})"
                    : $"{capture.GenerationId}  captured {QueryCommandSupport.FormatTimestamp(capture.CaptureTime!.Value)}  " +
                      $"{capture.Completeness!.Value.ToString().ToLowerInvariant()}  artifacts {capture.ArtifactCount}" +
                      (capture.PreviousGenerationId is null ? string.Empty : $"  previous {capture.PreviousGenerationId}");
                context.Stdout.WriteLine($"    {capture.AccountId}  {generation}");
            }
        }

        context.Stdout.WriteLine($"  ingest: {freshness.Ingest.Count} account(s)");
        foreach (var ingest in freshness.Ingest)
        {
            var progress = ingest.LastIngestAt is null
                ? "not ingested yet"
                : $"last ingest {QueryCommandSupport.FormatTimestamp(ingest.LastIngestAt.Value)}  " +
                  $"generation {ingest.LatestIngestedGenerationId ?? "(unknown)"}" +
                  (ingest.LatestIngestedConversationId is null ? string.Empty : $" ({ingest.LatestIngestedConversationId})");
            var scan = ingest.LastAccountScanAt is null
                ? "no account scan"
                : $"account scan {QueryCommandSupport.FormatTimestamp(ingest.LastAccountScanAt.Value)}";
            context.Stdout.WriteLine($"    {ingest.AccountId}  {progress}  {scan}");
        }
    }
}
