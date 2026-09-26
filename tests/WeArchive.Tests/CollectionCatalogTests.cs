using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Infrastructure.Collections;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

public sealed class CollectionCatalogTests
{
    [Fact]
    public async Task MissingCatalogIsAnEmptyCollectionList()
    {
        using var temp = new TempDirectory();
        var service = new CollectionCatalogService(new YamlCollectionCatalogStore(temp.Combine("collections.yaml")));
        Assert.Empty(await service.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReadsAndSortsDocumentedYamlShape()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("collections.yaml");
        await File.WriteAllTextAsync(path, """
            schema_version: 1.0
            collections:
              team:
                conversations:
                  - g_0123456789abcdef
              personal:
                conversations:
                  - u_0123456789abcdef
            """);
        var service = new CollectionCatalogService(new YamlCollectionCatalogStore(path));
        var result = await service.ListAsync(CancellationToken.None);
        Assert.Equal(["personal", "team"], result.Select(item => item.Name));
        Assert.Equal("g_0123456789abcdef", (await service.ResolveAsync("team", CancellationToken.None)).ConversationIds.Single());
    }

    [Theory]
    [InlineData("g_0123456789abcdef", "duplicate conversation ID")]
    [InlineData("conversation-name", "invalid stable conversation ID")]
    public async Task RejectsDuplicateOrMalformedMembership(string id, string expectedMessage)
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("collections.yaml");
        await File.WriteAllTextAsync(path, $"schema_version: 1.0\ncollections:\n  team:\n    conversations:\n      - {id}\n      - {id}\n");
        var service = new CollectionCatalogService(new YamlCollectionCatalogStore(path));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(CancellationToken.None));
        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCollectionHasDeterministicError()
    {
        using var temp = new TempDirectory();
        var service = new CollectionCatalogService(new YamlCollectionCatalogStore(temp.Combine("collections.yaml")));
        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ResolveAsync("missing", CancellationToken.None));
        Assert.Equal("Collection 'missing' was not found.", error.Message);
    }

    [Fact]
    public async Task RejectsUnsupportedSchemaWithoutRewritingCatalog()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("collections.yaml");
        const string yaml = "schema_version: 2.0\ncollections: {}\n";
        await File.WriteAllTextAsync(path, yaml);
        var service = new CollectionCatalogService(new YamlCollectionCatalogStore(path));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(CancellationToken.None));
        Assert.Equal(yaml, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task CollectionShowJsonEmitsOneDocumentWithStableMembership()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("collections.yaml");
        await File.WriteAllTextAsync(path, "schema_version: 1.0\ncollections:\n  team:\n    conversations:\n      - g_0123456789abcdef\n");
        var command = new CollectionCommand(new CollectionCatalogService(new YamlCollectionCatalogStore(path)));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, new StringWriter(), new GlobalOptions { Json = true });
        var exit = await command.ExecuteAsync(context, ["show", "team"], CancellationToken.None);
        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.ToString().TrimEnd();
        Assert.DoesNotContain('\n', output);
        using var json = System.Text.Json.JsonDocument.Parse(output);
        Assert.Equal("team", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("g_0123456789abcdef", json.RootElement.GetProperty("conversations")[0].GetString());
    }
}
