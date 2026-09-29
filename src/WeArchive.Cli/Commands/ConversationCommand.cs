using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive conversation list</c> and <c>wearchive conversation show &lt;id-or-alias&gt;</c>:
/// enumerate conversations for a source profile, or resolve and describe one conversation, with
/// stable identifiers and source-neutral metadata (docs/PRD.md FR-03, FR-22).
/// <para>
/// This is a thin adapter over <see cref="SourceCatalogService"/>: it contains no source-format
/// logic. Conversation stable ids mirror <see cref="StableIds.Conversation"/> exactly, so a
/// caller may refer to a conversation by the same identifier before and after import.
/// </para><para>
/// Account resolution never prompts: <c>--account &lt;id&gt;</c> selects explicitly (by stable
/// account id or source profile id); otherwise the current account is used, falling back to the
/// first account. This is safe under <c>--no-input</c>.
/// </para>
/// </summary>
public sealed class ConversationCommand : ICliCommand
{
    private readonly SourceCatalogService _catalog;

    public ConversationCommand(SourceCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Name => "conversation";

    public string Description => "List conversations or show one (list | show <id-or-alias>).";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "expected 'conversation list' or 'conversation show <id-or-alias>'.");
            return ExitCode.UsageError;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToList();

        if (string.Equals(subcommand, "list", StringComparison.OrdinalIgnoreCase))
            return await ListAsync(context, rest, cancellationToken);

        if (string.Equals(subcommand, "show", StringComparison.OrdinalIgnoreCase))
            return await ShowAsync(context, rest, cancellationToken);

        context.WriteError(
            CliErrorCode.UsageError,
            $"unknown conversation subcommand '{subcommand}'; expected 'list' or 'show'.");
        return ExitCode.UsageError;
    }

    // ---- conversation list ------------------------------------------------

    private async Task<int> ListAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var (account, positional, error) = ParseSubArguments(args);
        if (error is not null)
        {
            context.WriteError(CliErrorCode.UsageError, error);
            return ExitCode.UsageError;
        }

        if (positional.Count > 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[0]}' for 'conversation list'.");
            return ExitCode.UsageError;
        }

        var sourceProfileId = await ResolveAccountAsync(context, account, cancellationToken)
            .ConfigureAwait(false);
        if (sourceProfileId is null)
            return ExitCode.Failure;

        var accountId = StableIds.Account(_catalog.AdapterName, sourceProfileId);

        context.ReportProgress("Listing conversations…");

        IReadOnlyList<SourceConversation> conversations;
        try
        {
            conversations = await _catalog
                .ListConversationsAsync(sourceProfileId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.ConversationListFailed, ex.Message);
            return ExitCode.Failure;
        }

        var items = conversations
            .Select(c => MapConversation(c, accountId))
            .ToList();

        WriteListResult(context, sourceProfileId, items);
        return ExitCode.Success;
    }

    // ---- conversation show -------------------------------------------------

    private async Task<int> ShowAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var (account, positional, error) = ParseSubArguments(args);
        if (error is not null)
        {
            context.WriteError(CliErrorCode.UsageError, error);
            return ExitCode.UsageError;
        }

        if (positional.Count == 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "'conversation show' requires an <id-or-alias> argument.");
            return ExitCode.UsageError;
        }

        if (positional.Count > 1)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[1]}' for 'conversation show'.");
            return ExitCode.UsageError;
        }

        var idOrAlias = positional[0];

        var sourceProfileId = await ResolveAccountAsync(context, account, cancellationToken)
            .ConfigureAwait(false);
        if (sourceProfileId is null)
            return ExitCode.Failure;

        var accountId = StableIds.Account(_catalog.AdapterName, sourceProfileId);

        context.ReportProgress("Resolving conversation…");

        // The stable archive id is derived, not stored, so resolution requires listing the
        // profile's conversations and matching by stable id or by the source conversation id.
        SourceConversation? match = null;
        try
        {
            var conversations = await _catalog
                .ListConversationsAsync(sourceProfileId, cancellationToken)
                .ConfigureAwait(false);

            foreach (var conversation in conversations)
            {
                var stableId = StableIds.Conversation(
                    accountId, conversation.Kind, conversation.SourceConversationId,
                    conversation.PeerSourceUserId);

                if (string.Equals(stableId, idOrAlias, StringComparison.Ordinal) ||
                    string.Equals(conversation.SourceConversationId, idOrAlias, StringComparison.Ordinal))
                {
                    match = conversation;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.ConversationListFailed, ex.Message);
            return ExitCode.Failure;
        }

        if (match is null)
        {
            context.WriteError(
                CliErrorCode.ConversationNotFound,
                $"conversation '{idOrAlias}' was not found.");
            return ExitCode.Failure;
        }

        SourceConversationDetail detail;
        try
        {
            detail = await _catalog
                .DescribeConversationAsync(sourceProfileId, match.SourceConversationId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.WriteError(CliErrorCode.ConversationDescribeFailed, ex.Message);
            return ExitCode.Failure;
        }

        var dto = new ConversationDetailDto
        {
            StableId = StableIds.Conversation(
                accountId, match.Kind, match.SourceConversationId, match.PeerSourceUserId),
            SourceId = match.SourceConversationId,
            Kind = match.Kind.ToWireName(),
            Title = match.Title,
            PeerSourceUserId = match.PeerSourceUserId,
            MessageCount = detail.MessageCount,
            FirstMessageAt = detail.FirstMessageAt,
            LastMessageAt = detail.LastMessageAt,
            ParticipantCount = detail.ParticipantCount,
        };

        WriteDetailResult(context, dto);
        return ExitCode.Success;
    }

    // ---- account resolution ------------------------------------------------

    /// <summary>
    /// Resolves the source profile to enumerate. Returns the profile id on success, or null
    /// after writing a structured error (caller returns <see cref="ExitCode.Failure"/>).
    /// Resolution never prompts, so it is safe under <c>--no-input</c>. An explicit selector is
    /// matched by the shared <see cref="AccountSelector"/> contract — the exact (case-sensitive)
    /// canonical stable account id (<c>a_...</c>) or source profile id — the same contract
    /// <c>capture</c> uses (docs/CLI.md, Issue #47). A missing selector resolves deterministically
    /// to the current account, falling back to the first profile; an unmatched selector is an
    /// <c>account_not_found</c> failure, never a fallback.
    /// </summary>
    private async Task<string?> ResolveAccountAsync(
        CliContext context,
        string? account,
        CancellationToken cancellationToken)
    {
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
            context.WriteError(CliErrorCode.SourceUnavailable, ex.Message);
            return null;
        }

        if (accounts.Count == 0)
        {
            context.WriteError(
                CliErrorCode.NoAccounts,
                "no source profiles are available; cannot resolve a conversation profile.");
            return null;
        }

        if (account is null)
        {
            // Default to the current account, falling back to the first profile. This never
            // prompts: a missing selector is resolved deterministically, not interactively.
            var current = accounts.FirstOrDefault(a => a.IsCurrent) ?? accounts[0];
            return current.SourceProfileId;
        }

        foreach (var candidate in accounts)
        {
            if (AccountSelector.Matches(_catalog.AdapterName, candidate, account))
            {
                return candidate.SourceProfileId;
            }
        }

        context.WriteError(
            CliErrorCode.AccountNotFound,
            $"account '{account}' was not found.");
        return null;
    }

    // ---- argument parsing --------------------------------------------------

    /// <summary>
    /// Parses the conversation subcommand arguments: an optional <c>--account &lt;id&gt;</c>
    /// (space or <c>=</c> form), a bare <c>--</c> separator (everything after is positional, for
    /// identifiers that begin with <c>-</c>), and the remaining positional tokens. Unknown
    /// options are a usage error. The command-specific <c>--account</c> is parsed here rather
    /// than in the global <see cref="CommandLineParser"/> so global options stay generic.
    /// </summary>
    private static (string? Account, IReadOnlyList<string> Positional, string? Error) ParseSubArguments(
        IReadOnlyList<string> args)
    {
        string? account = null;
        var positional = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (token == "--")
            {
                for (i++; i < args.Count; i++)
                    positional.Add(args[i]);
                break;
            }

            const string accountPrefix = "--account=";
            if (token.StartsWith(accountPrefix, StringComparison.Ordinal))
            {
                account = token[accountPrefix.Length..];
                if (account.Length == 0)
                    return (null, positional, "option --account requires a value.");
                continue;
            }

            if (token == "--account")
            {
                if (i + 1 >= args.Count)
                    return (null, positional, "option --account requires a value.");
                account = args[++i];
                continue;
            }

            if (token.Length > 0 && token[0] == '-')
            {
                return (null, positional, $"unknown option '{token}'.");
            }

            positional.Add(token);
        }

        return (account, positional, null);
    }

    // ---- rendering ---------------------------------------------------------

    private static ConversationListItemDto MapConversation(SourceConversation c, string accountId) =>
        new()
        {
            StableId = StableIds.Conversation(accountId, c.Kind, c.SourceConversationId, c.PeerSourceUserId),
            SourceId = c.SourceConversationId,
            Kind = c.Kind.ToWireName(),
            Title = c.Title,
            PeerSourceUserId = c.PeerSourceUserId,
            LastMessageAt = c.LastMessageAt,
            MessageCountHint = c.MessageCountHint,
        };

    private static void WriteListResult(
        CliContext context,
        string sourceProfileId,
        IReadOnlyList<ConversationListItemDto> items)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(items));
            return;
        }

        context.Stdout.WriteLine($"Conversations ({items.Count}) [account: {sourceProfileId}]:");

        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            var title = string.IsNullOrEmpty(item.Title) ? "" : $"  {item.Title}";
            context.Stdout.WriteLine(
                $"  {item.StableId,-20} {item.Kind,-8} {item.SourceId}{title}");
        }
    }

    private static void WriteDetailResult(CliContext context, ConversationDetailDto dto)
    {
        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(dto));
            return;
        }

        context.Stdout.WriteLine("Conversation:");
        context.Stdout.WriteLine($"  stable id:        {dto.StableId}");
        context.Stdout.WriteLine($"  source id:        {dto.SourceId}");
        context.Stdout.WriteLine($"  kind:             {dto.Kind}");
        if (!string.IsNullOrEmpty(dto.Title))
            context.Stdout.WriteLine($"  title:            {dto.Title}");
        if (!string.IsNullOrEmpty(dto.PeerSourceUserId))
            context.Stdout.WriteLine($"  peer:             {dto.PeerSourceUserId}");
        context.Stdout.WriteLine($"  messages:         {dto.MessageCount}");
        if (dto.FirstMessageAt is not null)
            context.Stdout.WriteLine($"  first message at: {FormatTimestamp(dto.FirstMessageAt.Value)}");
        if (dto.LastMessageAt is not null)
            context.Stdout.WriteLine($"  last message at:  {FormatTimestamp(dto.LastMessageAt.Value)}");
        context.Stdout.WriteLine($"  participants:     {dto.ParticipantCount}");
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture);
}
