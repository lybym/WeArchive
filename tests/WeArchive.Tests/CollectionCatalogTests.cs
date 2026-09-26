using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;

namespace WeArchive.Tests;

/// <summary>
/// Unit tests for the Collection configuration model, validation and resolution
/// (docs/PRD.md FR-23, docs/HARNESS.md section 8,
/// docs/adr/0009-collection-configuration-ownership.md).
/// <para>
/// The catalog is validated in Core and never rewrites the user's configuration, so these tests
/// pin the deterministic reporting of invalid/duplicate membership and the deterministic failure of
/// an unusable configuration document.
/// </para>
/// </summary>
public sealed class CollectionCatalogTests
{
    private const string Valid = "g_0123456789abcdef";
    private const string Other = "u_fedcba9876543210";

    // ---- parsing -----------------------------------------------------------

    [Fact]
    public void ParseReadsNamesAndStableMembershipInDeclarationOrder()
    {
        var catalog = CollectionCatalog.Parse(Document(
            ("ai-toy", [Valid, Other]),
            ("east-product-center", [Other])), "collections.yaml");

        Assert.Equal(2, catalog.Collections.Count);
        Assert.Equal(["ai-toy", "east-product-center"], catalog.Collections.Select(c => c.Name));

        var aiToy = Assert.Single(catalog.Collections, c => c.Name == "ai-toy");
        Assert.Equal([Valid, Other], aiToy.ConversationIds);
        Assert.Empty(aiToy.InvalidConversationIds);
        Assert.Empty(aiToy.DuplicateConversationIds);
    }

    [Fact]
    public void ParseTreatsAnAbsentDocumentAsAnEmptyCatalog()
    {
        // A user who has not created a Collection yet gets an empty list, not a failure.
        var catalog = CollectionCatalog.Parse(null, "C:\\data\\collections.yaml");

        Assert.Empty(catalog.Collections);
        Assert.Equal("C:\\data\\collections.yaml", catalog.ConfigurationLocation);
    }

    [Fact]
    public void ParseAcceptsAnEmptyMembershipList()
    {
        var catalog = CollectionCatalog.Parse(Document(("empty", [])), "collections.yaml");

        var collection = Assert.Single(catalog.Collections);
        Assert.Empty(collection.ConversationIds);
    }

    [Fact]
    public void ParseRejectsAnUnsupportedSchemaVersion()
    {
        var document = Document(("ai-toy", [Valid]));
        document.SchemaVersion = "2.0";

        var error = Assert.Throws<CollectionConfigurationException>(
            () => CollectionCatalog.Parse(document, "collections.yaml"));

        Assert.Contains("schema_version", error.Message, StringComparison.Ordinal);
        Assert.Contains("collections.yaml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAMissingCollectionsMapping()
    {
        var error = Assert.Throws<CollectionConfigurationException>(
            () => CollectionCatalog.Parse(new CollectionCatalogDocument { SchemaVersion = "1.0" }, "collections.yaml"));

        Assert.Contains("collections", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRejectsAnEmptyCollectionName()
    {
        var error = Assert.Throws<CollectionConfigurationException>(
            () => CollectionCatalog.Parse(Document(("  ", [Valid])), "collections.yaml"));

        Assert.Contains("name is empty", error.Reason, StringComparison.Ordinal);
    }

    // ---- duplicate membership ----------------------------------------------

    [Fact]
    public void DuplicateMembershipIsReportedAndResolvedOnce()
    {
        var catalog = CollectionCatalog.Parse(Document(("ai-toy", [Valid, Other, Valid, Valid])), "collections.yaml");

        var collection = Assert.Single(catalog.Collections);
        // The resolved membership is de-duplicated in first-declaration order...
        Assert.Equal([Valid, Other], collection.ConversationIds);
        // ...and the repetitions are reported rather than silently dropped.
        Assert.Equal([Valid, Valid], collection.DuplicateConversationIds);
        // Every declared entry is still visible exactly as the user wrote it.
        Assert.Equal([Valid, Other, Valid, Valid], collection.DeclaredConversationIds);
    }

    // ---- stable-id validation ----------------------------------------------

    [Theory]
    [InlineData("g_0123456789abcdef", true)]
    [InlineData("u_fedcba9876543210", true)]
    [InlineData("g_0123456789ABCDEF", false)] // uppercase is not the derived form
    [InlineData("G_0123456789abcdef", false)]
    [InlineData("m_0123456789abcdef", false)] // messages are not conversation membership
    [InlineData("a_0123456789abcdef", false)] // accounts are not conversations
    [InlineData("g_0123456789abcde", false)] // too short
    [InlineData("g_0123456789abcdef0", false)] // too long
    [InlineData("g-0123456789abcdef", false)]
    [InlineData("not-an-id", false)]
    [InlineData("wxid_alice", false)]
    [InlineData("100200300@chatroom", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StableConversationIdValidationIsExact(string? value, bool expected) =>
        Assert.Equal(expected, CollectionCatalog.IsStableConversationId(value));

    [Fact]
    public void InvalidMembershipEntriesAreReportedAndExcludedFromResolvedMembership()
    {
        var catalog = CollectionCatalog.Parse(
            Document(("ai-toy", [Valid, "wxid_alice", "g_0123", "G_0123456789ABCDEF", Other])),
            "collections.yaml");

        var collection = Assert.Single(catalog.Collections);
        Assert.Equal([Valid, Other], collection.ConversationIds);
        Assert.Equal(["wxid_alice", "g_0123", "G_0123456789ABCDEF"], collection.InvalidConversationIds);
    }

    // ---- resolution --------------------------------------------------------

    [Fact]
    public async Task ResolveReturnsTheDeclaredCollection()
    {
        var service = new CollectionCatalogService(new StubSource(
            Document(("ai-toy", [Valid, Other])), "C:\\data\\collections.yaml"));

        var collection = await service.ResolveAsync("ai-toy", CancellationToken.None);

        Assert.Equal("ai-toy", collection.Name);
        Assert.Equal([Valid, Other], collection.ConversationIds);
    }

    [Fact]
    public async Task ResolveOfAnUnknownNameIsDeterministic()
    {
        var service = new CollectionCatalogService(new StubSource(
            Document(("ai-toy", [Valid])), "C:\\data\\collections.yaml"));

        var error = await Assert.ThrowsAsync<CollectionNotFoundException>(
            () => service.ResolveAsync("missing", CancellationToken.None));

        Assert.Equal("missing", error.Name);
        Assert.Contains("C:\\data\\collections.yaml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveIsCaseSensitive()
    {
        var service = new CollectionCatalogService(new StubSource(
            Document(("ai-toy", [Valid])), "collections.yaml"));

        await Assert.ThrowsAsync<CollectionNotFoundException>(
            () => service.ResolveAsync("AI-Toy", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveWithoutAConfigurationIsAnUnknownName()
    {
        // `collection list` reports an empty catalog, and resolving a name from it is a
        // deterministic not-found rather than a crash.
        var service = new CollectionCatalogService(new StubSource(null, string.Empty));

        var catalog = await service.LoadAsync(CancellationToken.None);
        Assert.Empty(catalog.Collections);

        await Assert.ThrowsAsync<CollectionNotFoundException>(
            () => service.ResolveAsync("ai-toy", CancellationToken.None));
    }

    private static CollectionCatalogDocument Document(
        params (string Name, string[] Conversations)[] collections) => new()
        {
            SchemaVersion = "1.0",
            Collections = collections.ToDictionary(
                entry => entry.Name,
                entry => new CollectionEntryDocument { Conversations = [.. entry.Conversations] },
                StringComparer.Ordinal),
        };

    private sealed class StubSource(CollectionCatalogDocument? document, string location) : ICollectionCatalogSource
    {
        public string Location => location;

        public Task<CollectionCatalogDocument?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(document);
    }
}