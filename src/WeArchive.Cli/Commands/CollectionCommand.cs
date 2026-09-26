using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Output;
using WeArchive.Core.Services;

namespace WeArchive.Cli.Commands;

public sealed class CollectionCommand(CollectionCatalogService catalog) : ICliCommand
{
    public string Name => "collection";
    public string Description => "List collections or show stable conversation membership.";

    public async Task<int> ExecuteAsync(CliContext context, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 1 && args[0] == "list")
        {
            var collections = await catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            if (context.Options.Json)
                context.Stdout.WriteLine(CliJson.Serialize(new { collections = collections.Select(item => item.Name).ToArray() }));
            else
                foreach (var collection in collections) context.Stdout.WriteLine(collection.Name);
            return ExitCode.Success;
        }
        if (args.Count == 2 && args[0] == "show")
        {
            var collection = await catalog.ResolveAsync(args[1], cancellationToken).ConfigureAwait(false);
            if (context.Options.Json)
                context.Stdout.WriteLine(CliJson.Serialize(new { name = collection.Name, conversations = collection.ConversationIds }));
            else
            {
                context.Stdout.WriteLine(collection.Name);
                foreach (var id in collection.ConversationIds) context.Stdout.WriteLine($"  {id}");
            }
            return ExitCode.Success;
        }
        throw new CliUsageException("usage: collection list | collection show <name>");
    }
}
