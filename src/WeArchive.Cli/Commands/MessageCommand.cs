using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive message list (--conversation &lt;stable-id&gt; | --collection &lt;name&gt;)
/// [--since] [--until] [--participant] [--type] [--limit] [--cursor]</c>: bounded,
/// cursor-paginated retrieval of canonical archived messages over one conversation or one
/// Collection's resolved membership (docs/CLI.md, docs/PRD.md FR-16/FR-23/FR-30).
/// <para>
/// This is a thin transport over <see cref="ArchiveQueryService"/>: it parses options and renders
/// the result, and owns no filter, scope, cursor, paging or ordering rule. It depends on no source
/// adapter, so it works while live WeChat is unavailable and reads only the canonical archive.
/// </para>
/// </summary>
public sealed class MessageCommand : ICliCommand
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

        options.TryGetValue("--conversation", out var conversation);
        options.TryGetValue("--collection", out var collection);

        // Scope parsing stays a usage decision, exactly as the sync, search and stats commands
        // scope theirs: exactly one of the two, and neither is a usage failure rather than a
        // silently widened listing.
        if (conversation is not null && collection is not null)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "message list accepts either --conversation <stable-conversation-id> or --collection <name>, not both.");
            return ExitCode.UsageError;
        }

        if (conversation is null && collection is null)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "'message list' requires --conversation <stable-conversation-id> or --collection <name>.");
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
            CollectionName = collection,
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

        WriteResult(context, result);
        return ExitCode.Success;
    }

    private static void WriteResult(CliContext context, MessageQueryPage page)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(new MessageListResultDto
            {
                Scope = MessageListScopeDto.From(page.Scope),
                Items = [.. page.Items.Select(MessageDto.From)],
                NextCursor = page.NextCursor,
                HasMore = page.HasMore,
            }));

            return;
        }

        context.Stdout.WriteLine(
            $"Messages ({page.Items.Count}) [{DescribeScope(page.Scope)}{(page.HasMore ? ", more available" : string.Empty)}]:");

        foreach (var message in page.Items)
            context.Stdout.WriteLine($"  {QueryCommandSupport.Line(MessageDto.From(message))}");

        if (page.NextCursor is not null)
            context.Stdout.WriteLine($"next cursor: {page.NextCursor}");
    }

    /// <summary>
    /// One deterministic human scope description. The archived-member ratio is always shown for a
    /// Collection so a canonical archive behind its configuration is visible in human output too;
    /// invalid and duplicate membership entries appear only when the configuration carries them.
    /// </summary>
    private static string DescribeScope(MessageQueryScope scope) => scope.Kind switch
    {
        "collection" => string.Concat(
            $"collection: {scope.CollectionName} ",
            $"({scope.ArchivedMemberConversationCount}/{scope.MemberConversationCount} members archived",
            scope.InvalidMemberCount > 0 ? $", {scope.InvalidMemberCount} invalid" : string.Empty,
            scope.DuplicateMemberCount > 0 ? $", {scope.DuplicateMemberCount} duplicate" : string.Empty,
            ")"),
        _ => $"conversation: {scope.ConversationId}",
    };
}
