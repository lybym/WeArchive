using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive stats [--conversation] [--collection] [--since] [--until] [--bucket] [--limit]
/// [--cursor]</c>: one bounded, read-only statistics report over the canonical archive
/// (docs/CLI.md, docs/DATA_MODEL.md section 24, docs/PRD.md FR-16/FR-17).
/// <para>
/// This is a thin transport over <see cref="ArchiveQueryService.GetStatisticsAsync"/>: the service
/// owns scope resolution, filter validation and the opaque breakdown cursor, and the archive store
/// derives every count from canonical rows at query time. This command only parses the documented
/// options and renders the report; it depends on no source adapter, touches no Raw Vault and never
/// writes the archive.
/// </para>
/// </summary>
public sealed class StatsCommand : ICliCommand
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "--conversation",
        "--collection",
        "--since",
        "--until",
        "--bucket",
        "--limit",
        "--cursor",
    };

    private readonly ArchiveQueryService _query;

    public StatsCommand(ArchiveQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public string Name => "stats";

    public string Description =>
        "Report read-only canonical archive statistics and activity timeline.";

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

        if (positional.Count > 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "unexpected argument for 'stats'; statistics take options only.");
            return ExitCode.UsageError;
        }

        int? limit = null;
        if (options.TryGetValue("--limit", out var limitText))
        {
            if (!QueryCommandSupport.TryParseCount(limitText, "--limit", out var parsed, out var limitError))
            {
                context.WriteError(CliErrorCode.UsageError, limitError);
                return ExitCode.UsageError;
            }

            limit = parsed;
        }

        var request = new StatisticsRequest
        {
            ConversationId = options.GetValueOrDefault("--conversation"),
            CollectionName = options.GetValueOrDefault("--collection"),
            Since = options.GetValueOrDefault("--since"),
            Until = options.GetValueOrDefault("--until"),
            Bucket = options.GetValueOrDefault("--bucket"),
            Limit = limit,
            Cursor = options.GetValueOrDefault("--cursor"),
        };

        context.ReportProgress("Counting canonical archive records…");

        ArchiveStatistics result;
        try
        {
            result = await _query.GetStatisticsAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveQueryException ex)
        {
            return QueryCommandSupport.ReportQueryFailure(context, ex);
        }

        WriteResult(context, result);
        return ExitCode.Success;
    }

    private static void WriteResult(CliContext context, ArchiveStatistics statistics)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(StatsResultDto.From(statistics)));
            return;
        }

        var scope = statistics.ScopeKind switch
        {
            "conversation" => $"conversation {statistics.ConversationId}",
            "collection" => $"collection {statistics.CollectionName}",
            _ => "whole archive",
        };
        context.Stdout.WriteLine($"Statistics ({scope}):");

        var totals = statistics.Totals;
        context.Stdout.WriteLine(
            $"  messages: {totals.MessageCount}  conversations: {totals.ConversationCount}  " +
            $"senders: {totals.SenderCount}  unattributed: {totals.UnattributedCount}");
        context.Stdout.WriteLine(
            $"  unknown: {totals.UnknownCount}  partial: {totals.PartialCount}");
        if (totals.FirstMessageAt is not null || totals.LastMessageAt is not null)
        {
            var first = totals.FirstMessageAt is null ? "(none)" : QueryCommandSupport.FormatTimestamp(totals.FirstMessageAt.Value);
            var last = totals.LastMessageAt is null ? "(none)" : QueryCommandSupport.FormatTimestamp(totals.LastMessageAt.Value);
            context.Stdout.WriteLine($"  first: {first}  last: {last}");
        }

        context.Stdout.WriteLine($"  as of: {QueryCommandSupport.FormatTimestamp(statistics.QueriedAt)}  basis: {statistics.ArchivePath}");

        context.Stdout.WriteLine("By type:");
        foreach (var type in statistics.ByType)
            context.Stdout.WriteLine($"  {type.Type}  {type.Count}");

        context.Stdout.WriteLine(
            $"By {statistics.Breakdown.ToWireName()} ({statistics.BreakdownItems.Count})" +
            $"{(statistics.HasMore ? " [more available]" : string.Empty)}:");
        foreach (var row in statistics.BreakdownItems)
        {
            var key = row.BucketStart is not null
                ? QueryCommandSupport.FormatTimestamp(row.BucketStart.Value)
                : row.ConversationId ?? "(unresolved)";
            context.Stdout.WriteLine(
                $"  {key}  {row.MessageCount}  (unknown {row.UnknownCount}, partial {row.PartialCount})");
        }

        if (statistics.NextCursor is not null)
            context.Stdout.WriteLine($"next cursor: {statistics.NextCursor}");
    }
}
