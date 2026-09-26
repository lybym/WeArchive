using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Collections;

namespace WeArchive.Cli.Commands;

/// <summary>
/// <c>wearchive collection list</c> and <c>wearchive collection show &lt;name&gt;</c>: enumerate the
/// named Collections and their stable conversation membership (docs/PRD.md FR-23/FR-29,
/// docs/HARNESS.md section 8).
/// <para>
/// This is a thin transport adapter over <see cref="CollectionCatalogService"/>. It reads the one
/// authoritative application-level configuration and never writes it: this Issue adds no
/// interactive Collection editor, and user-maintained configuration is diagnosed rather than
/// silently repaired. Unknown names, invalid configuration and invalid/duplicate membership entries
/// are all reported deterministically.
/// </para><para>
/// Membership is reported as stable conversation IDs (<c>g_…</c>/<c>u_…</c>), never display names,
/// so a Collection stays addressable after a rename or re-import.
/// </para>
/// </summary>
public sealed class CollectionCommand : ICliCommand
{
    private readonly CollectionCatalogService _catalog;

    public CollectionCommand(CollectionCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public string Name => "collection";

    public string Description => "List Collections or show one (list | show <name>).";

    public async Task<int> ExecuteAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                "expected 'collection list' or 'collection show <name>'.");
            return ExitCode.UsageError;
        }

        var subcommand = args[0];
        var rest = args.Skip(1).ToList();

        if (string.Equals(subcommand, "list", StringComparison.OrdinalIgnoreCase))
            return await ListAsync(context, rest, cancellationToken).ConfigureAwait(false);

        if (string.Equals(subcommand, "show", StringComparison.OrdinalIgnoreCase))
            return await ShowAsync(context, rest, cancellationToken).ConfigureAwait(false);

        context.WriteError(
            CliErrorCode.UsageError,
            $"unknown collection subcommand '{subcommand}'; expected 'list' or 'show'.");
        return ExitCode.UsageError;
    }

    // ---- collection list ---------------------------------------------------

    private async Task<int> ListAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var (positional, error) = ParseSubArguments(args);
        if (error is not null)
        {
            context.WriteError(CliErrorCode.UsageError, error);
            return ExitCode.UsageError;
        }

        if (positional.Count > 0)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[0]}' for 'collection list'.");
            return ExitCode.UsageError;
        }

        CollectionCatalog catalog;
        try
        {
            catalog = await _catalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CollectionConfigurationException ex)
        {
            context.WriteError(CliErrorCode.CollectionConfigInvalid, ex.Message);
            return ExitCode.UsageError;
        }

        var items = catalog.Collections.Select(collection => new CollectionListItemDto
        {
            Name = collection.Name,
            ConversationCount = collection.ConversationIds.Count,
            InvalidMemberCount = collection.InvalidConversationIds.Count,
            DuplicateMemberCount = collection.DuplicateConversationIds.Count,
        }).ToList();

        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(items));
            return ExitCode.Success;
        }

        context.Stdout.WriteLine($"Collections ({items.Count}):");
        foreach (var item in items)
        {
            context.Stdout.WriteLine(
                $"  {item.Name,-24} {item.ConversationCount} conversation(s)");
        }

        return ExitCode.Success;
    }

    // ---- collection show ---------------------------------------------------

    private async Task<int> ShowAsync(
        CliContext context,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var (positional, error) = ParseSubArguments(args);
        if (error is not null)
        {
            context.WriteError(CliErrorCode.UsageError, error);
            return ExitCode.UsageError;
        }

        if (positional.Count == 0)
        {
            context.WriteError(CliErrorCode.UsageError, "'collection show' requires a <name> argument.");
            return ExitCode.UsageError;
        }

        if (positional.Count > 1)
        {
            context.WriteError(
                CliErrorCode.UsageError,
                $"unexpected argument '{positional[1]}' for 'collection show'.");
            return ExitCode.UsageError;
        }

        var name = positional[0];

        CollectionDefinition definition;
        try
        {
            definition = await _catalog.ResolveAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CollectionConfigurationException ex)
        {
            context.WriteError(CliErrorCode.CollectionConfigInvalid, ex.Message);
            return ExitCode.UsageError;
        }
        catch (CollectionNotFoundException ex)
        {
            // A name that resolves to nothing is an operation failure (exit 1), matching
            // `conversation show`'s conversation_not_found rather than a usage syntax error.
            context.WriteError(CliErrorCode.CollectionNotFound, ex.Message);
            return ExitCode.Failure;
        }

        var dto = new CollectionDetailDto
        {
            Name = definition.Name,
            ConversationIds = definition.ConversationIds,
            InvalidConversationIds = definition.InvalidConversationIds,
            DuplicateConversationIds = definition.DuplicateConversationIds,
        };

        if (context.Options.Json)
        {
            context.Stdout.WriteLine(CliJson.Serialize(dto));
            return ExitCode.Success;
        }

        context.Stdout.WriteLine($"Collection: {dto.Name}");
        context.Stdout.WriteLine($"  conversations: {dto.ConversationIds.Count}");
        foreach (var conversationId in dto.ConversationIds)
        {
            context.Stdout.WriteLine($"    {conversationId}");
        }

        if (dto.InvalidConversationIds.Count > 0)
        {
            context.Stdout.WriteLine($"  invalid entries: {dto.InvalidConversationIds.Count}");
            foreach (var invalid in dto.InvalidConversationIds)
            {
                context.Stdout.WriteLine($"    {invalid}");
            }
        }

        if (dto.DuplicateConversationIds.Count > 0)
        {
            context.Stdout.WriteLine($"  duplicate entries: {dto.DuplicateConversationIds.Count}");
            foreach (var duplicate in dto.DuplicateConversationIds)
            {
                context.Stdout.WriteLine($"    {duplicate}");
            }
        }

        return ExitCode.Success;
    }

    // ---- argument parsing --------------------------------------------------

    /// <summary>
    /// Parses the collection subcommand arguments: a bare <c>--</c> separator (everything after is
    /// positional, for names beginning with <c>-</c>) and the remaining positional tokens. Unknown
    /// options are a usage error.
    /// </summary>
    private static (IReadOnlyList<string> Positional, string? Error) ParseSubArguments(
        IReadOnlyList<string> args)
    {
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

            if (token.Length > 0 && token[0] == '-')
            {
                return (positional, $"unknown option '{token}'.");
            }

            positional.Add(token);
        }

        return (positional, null);
    }
}