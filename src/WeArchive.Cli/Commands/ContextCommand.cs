using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive context &lt;message-id-or-reference&gt; [--before &lt;n&gt;] [--after &lt;n&gt;]</c>:
/// the bounded canonical window around one stable message ID (docs/CLI.md, docs/PRD.md FR-16/FR-30).
/// The target may also be a citation-ready <see cref="EvidenceReference"/> (issue #107,
/// docs/HARNESS.md section 11), which resolves to exactly the cited window or fails explicitly.
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
            context.WriteError(
                CliErrorCode.UsageError,
                "'context' requires a stable message id or an evidence reference.");
            return ExitCode.UsageError;
        }

        if (positional.Count > 1)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[1]}' for 'context'.");
            return ExitCode.UsageError;
        }

        int? beforeOverride = null;
        int? afterOverride = null;

        if (options.TryGetValue("--before", out var beforeText))
        {
            if (!QueryCommandSupport.TryParseCount(beforeText, "--before", out var before, out var beforeError))
            {
                context.WriteError(CliErrorCode.UsageError, beforeError);
                return ExitCode.UsageError;
            }

            beforeOverride = before;
        }

        if (options.TryGetValue("--after", out var afterText))
        {
            if (!QueryCommandSupport.TryParseCount(afterText, "--after", out var after, out var afterError))
            {
                context.WriteError(CliErrorCode.UsageError, afterError);
                return ExitCode.UsageError;
            }

            afterOverride = after;
        }

        // The target is either a bare stable message id (the shipped form) or a citation-ready
        // evidence reference, which carries its own cited window and conversation. An explicit
        // --before/--after overrides the cited window; the cited conversation is always verified.
        var target = positional[0];
        EvidenceReference? reference = null;
        int requestedBefore;
        int requestedAfter;
        if (target.StartsWith(EvidenceReference.UriPrefix, StringComparison.Ordinal))
        {
            if (!EvidenceReference.TryParse(target, out reference, out var referenceError))
            {
                context.WriteError(CliErrorCode.UsageError, referenceError);
                return ExitCode.UsageError;
            }

            requestedBefore = beforeOverride ?? reference.Before;
            requestedAfter = afterOverride ?? reference.After;
        }
        else
        {
            requestedBefore = beforeOverride ?? ArchiveQueryService.DefaultContextMessages;
            requestedAfter = afterOverride ?? ArchiveQueryService.DefaultContextMessages;
        }

        context.ReportProgress("Reading message context…");

        MessageContextWindow window;
        try
        {
            window = reference is null
                ? await _query.GetContextAsync(target, requestedBefore, requestedAfter, cancellationToken)
                    .ConfigureAwait(false)
                : await _query.GetContextAsync(reference, beforeOverride, afterOverride, cancellationToken)
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

        WriteResult(context, window, requestedBefore, requestedAfter);
        return ExitCode.Success;
    }

    private static void WriteResult(
        CliContext context,
        MessageContextWindow window,
        int requestedBefore,
        int requestedAfter)
    {
        var target = MessageDto.From(window.Message);
        var reference = EvidenceReference.Create(
            target.Id,
            target.ConversationId,
            requestedBefore,
            requestedAfter);

        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(new MessageContextResultDto
            {
                MessageId = target.Id,
                ConversationId = target.ConversationId,
                Reference = reference.ToCanonicalForm(),
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
        context.Stdout.WriteLine($"reference: {reference.ToCanonicalForm()}");
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