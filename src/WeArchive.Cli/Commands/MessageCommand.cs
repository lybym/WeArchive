using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive message list --conversation &lt;stable-id&gt; [--since] [--until]
/// [--participant] [--type] [--limit] [--cursor]</c>: bounded, cursor-paginated retrieval of
/// canonical archived messages (docs/CLI.md, docs/PRD.md FR-16/FR-30).
/// <para>
/// This is a thin transport over <see cref="ArchiveQueryService"/>: it parses options and renders
/// the result, and owns no filter, cursor, paging or ordering rule. It depends on no source
/// adapter, so it works while live WeChat is unavailable and reads only the canonical archive.
/// </para>
/// </summary>
public sealed class MessageCommand : ICliCommand
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "--conversation",
        "--since",
        "--until",
        "--participant",
        "--type",
        "--limit",
        "--cursor",
    };

    private readonly ArchiveQueryService _query;

    public MessageCommand(ArchiveQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public string Name => "message";

    public string Description => "List canonical archived messages (list).";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            context.WriteError(CliErrorCode.UsageError, "expected 'message list'.");
            return ExitCode.UsageError;
        }

        var subcommand = args[0];
        if (!string.Equals(subcommand, "list", StringComparison.OrdinalIgnoreCase))
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unknown message subcommand '{subcommand}'; expected 'list'.");
            return ExitCode.UsageError;
        }

        return await ListAsync(context, [.. args.Skip(1)], cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ListAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
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
                $"unexpected argument '{positional[0]}' for 'message list'.");
            return ExitCode.UsageError;
        }

        if (!options.TryGetValue("--conversation", out var conversation))
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "'message list' requires --conversation <stable-conversation-id>.");
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

        var request = new MessageListRequest
        {
            ConversationId = conversation,
            Since = options.GetValueOrDefault("--since"),
            Until = options.GetValueOrDefault("--until"),
            ParticipantId = options.GetValueOrDefault("--participant"),
            Type = options.GetValueOrDefault("--type"),
            Limit = limit,
            Cursor = options.GetValueOrDefault("--cursor"),
        };

        context.ReportProgress("Querying archived messages…");

        MessageQueryPage result;
        try
        {
            result = await _query.ListMessagesAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveQueryException ex)
        {
            return QueryCommandSupport.ReportQueryFailure(context, ex);
        }

        WriteResult(context, conversation, result);
        return ExitCode.Success;
    }

    private static void WriteResult(CliContext context, string conversationId, MessageQueryPage page)
    {
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
            $"Messages ({page.Items.Count}) [conversation: {conversationId}{(page.HasMore ? ", more available" : string.Empty)}]:");

        foreach (var message in page.Items)
            context.Stdout.WriteLine($"  {QueryCommandSupport.Line(MessageDto.From(message))}");

        if (page.NextCursor is not null)
            context.Stdout.WriteLine($"next cursor: {page.NextCursor}");
    }
}