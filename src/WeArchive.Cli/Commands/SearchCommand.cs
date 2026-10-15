using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Query;
using WeArchive.Core.Search;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive search &lt;query&gt; [--conversation] [--collection] [--since] [--until]
/// [--participant] [--type] [--limit] [--cursor]</c>: bounded canonical full-text retrieval over
/// the derived FTS index (docs/SEARCH_SPEC.md sections 9.2 and 12, docs/CLI.md, docs/PRD.md
/// FR-15/FR-16/FR-17).
/// <para>
/// This is a thin transport over <see cref="ArchiveQueryService.SearchAsync"/>: the service owns
/// the query grammar and its fail-closed validation, the CJK bigram projection of every term,
/// filter validation, Collection scope resolution and the opaque search cursor; this command only
/// parses the documented options and renders the page. It depends on no source adapter, so it
/// works while live WeChat is unavailable and reads only the canonical archive.
/// </para>
/// <para>
/// The query is one positional argument; a multi-term query is quoted as a whole
/// (<c>wearchive search "开会 预算"</c>). Extra positional arguments are a usage failure rather
/// than a silently widened query, so caller input can never be reinterpreted.
/// </para>
/// </summary>
public sealed class SearchCommand : ICliCommand
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "--conversation",
        "--collection",
        "--since",
        "--until",
        "--participant",
        "--type",
        "--limit",
        "--cursor",
    };

    private readonly ArchiveQueryService _query;

    public SearchCommand(ArchiveQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public string Name => "search";

    public string Description => "Search canonical archived messages by full-text query.";

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

        if (positional.Count == 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "'search' requires a query string; quote a multi-term query as one argument.");
            return ExitCode.UsageError;
        }

        if (positional.Count > 1)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "unexpected extra argument for 'search'; quote the whole query as one argument.");
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

        var request = new SearchRequest
        {
            Query = positional[0],
            ConversationId = options.GetValueOrDefault("--conversation"),
            CollectionName = options.GetValueOrDefault("--collection"),
            Since = options.GetValueOrDefault("--since"),
            Until = options.GetValueOrDefault("--until"),
            ParticipantId = options.GetValueOrDefault("--participant"),
            Type = options.GetValueOrDefault("--type"),
            Limit = limit,
            Cursor = options.GetValueOrDefault("--cursor"),
        };

        context.ReportProgress("Searching canonical messages…");

        SearchPage result;
        try
        {
            result = await _query.SearchAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static void WriteResult(CliContext context, SearchPage page)
    {
        // The search envelope is byte-shaped like the `message list` page
        // (docs/SEARCH_SPEC.md section 9.2): same DTO, same pinned field names.
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(new MessageListResultDto
            {
                Items = [.. page.Items.Select(MessageDto.From)],
                NextCursor = page.NextCursor,
                HasMore = page.HasMore,
            }));

            return;
        }

        context.Stdout.WriteLine(
            $"Hits ({page.Items.Count}){(page.HasMore ? " [more available]" : string.Empty)}:");

        // One line per hit led by the stable message id, because that is what a human feeds back
        // into `context` (docs/SEARCH_SPEC.md section 9.2). The query itself is never echoed.
        foreach (var message in page.Items)
            context.Stdout.WriteLine($"  {QueryCommandSupport.Line(MessageDto.From(message))}");

        if (page.NextCursor is not null)
            context.Stdout.WriteLine($"next cursor: {page.NextCursor}");
    }
}
