using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive context &lt;message-id&gt; [--before &lt;n&gt;] [--after &lt;n&gt;]</c>: the bounded
/// canonical window around one stable message ID (docs/CLI.md, docs/PRD.md FR-16/FR-30).
/// <para>
/// The target message is rendered as a field of its own, distinct from the arrays before and after
/// it, so a machine caller never has to infer the anchor from a list position. Like
/// <c>message list</c> it depends on no source adapter and reads only the canonical archive.
/// </para>
/// </summary>
public sealed class ContextCommand : ICliCommand
{
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "--before",
        "--after",
    };

    private readonly ArchiveQueryService _query;

    public ContextCommand(ArchiveQueryService query)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
    }

    public string Name => "context";

    public string Description => "Show one canonical message with its surrounding context.";

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
            context.WriteError(CliErrorCode.UsageError, "'context' requires a stable message id.");
            return ExitCode.UsageError;
        }

        if (positional.Count > 1)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[1]}' for 'context'.");
            return ExitCode.UsageError;
        }

        var before = ArchiveQueryService.DefaultContextMessages;
        var after = ArchiveQueryService.DefaultContextMessages;

        if (options.TryGetValue("--before", out var beforeText))
        {
            if (!QueryCommandSupport.TryParseCount(beforeText, "--before", out before, out var beforeError))
            {
                context.WriteError(CliErrorCode.UsageError, beforeError);
                return ExitCode.UsageError;
            }
        }

        if (options.TryGetValue("--after", out var afterText))
        {
            if (!QueryCommandSupport.TryParseCount(afterText, "--after", out after, out var afterError))
            {
                context.WriteError(CliErrorCode.UsageError, afterError);
                return ExitCode.UsageError;
            }
        }

        var messageId = positional[0];
        context.ReportProgress("Reading message context…");

        MessageContextWindow window;
        try
        {
            window = await _query.GetContextAsync(messageId, before, after, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArchiveQueryException ex)
        {
            return QueryCommandSupport.ReportQueryFailure(context, ex);
        }

        WriteResult(context, window);
        return ExitCode.Success;
    }

    private static void WriteResult(CliContext context, MessageContextWindow window)
    {
        var target = MessageDto.From(window.Message);

        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(new MessageContextResultDto
            {
                MessageId = target.Id,
                ConversationId = target.ConversationId,
                Before = [.. window.Before.Select(MessageDto.From)],
                Message = target,
                After = [.. window.After.Select(MessageDto.From)],
            }));

            return;
        }

        context.Stdout.WriteLine($"Message {target.Id} [conversation: {target.ConversationId}]:");
        WriteWindow(context, "before", window.Before);
        context.Stdout.WriteLine("  message:");
        context.Stdout.WriteLine($"    {QueryCommandSupport.Line(target)}");
        WriteWindow(context, "after", window.After);
    }

    private static void WriteWindow(
        CliContext context,
        string label,
        IReadOnlyList<CanonicalMessage> messages)
    {
        context.Stdout.WriteLine($"  {label} ({messages.Count}):");
        foreach (var message in messages)
            context.Stdout.WriteLine($"    {QueryCommandSupport.Line(MessageDto.From(message))}");
    }
}