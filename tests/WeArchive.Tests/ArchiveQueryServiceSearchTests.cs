using System.Text.Json.Nodes;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.Query;
using WeArchive.Core.RawVault;
using WeArchive.Core.Search;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Canonical search at the application service boundary ([Issue #102](
/// https://github.com/lybym/WeArchive/issues/102), M3b-1, docs/SEARCH_SPEC.md sections 4–9,
/// docs/ARCHITECTURE.md section 3.9).
/// <para>
/// Covers the projection and searchable-field inventory of the index input, the query grammar and
/// its fail-closed validation, the full #101 recall baseline over the deterministic corpus fixtures
/// through the real archive, bm25 ranking with the canonical tie-break, the section 7 filters
/// (including Collection scope resolution), keyset cursor pagination, and the failure modes —
/// always through <see cref="ArchiveQueryService"/> and the real
/// <see cref="SqliteArchiveStore"/> write path. No CLI surface is involved: the
/// <c>wearchive search</c> transport is the follow-up slice (issue #103).
/// </para>
/// </summary>
public sealed class ArchiveQueryServiceSearchTests
{
    // --------------------------------------------------------------------------------------------
    // Index input: CJK bigram projection and searchable-field inventory
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void ProjectionFollowsTheSpecifiedBigramRules()
    {
        Assert.Equal(string.Empty, CjkBigramProjection.Project(string.Empty));
        Assert.Equal("model card", CjkBigramProjection.Project("model card"));
        Assert.Equal("模型", CjkBigramProjection.Project("模型"));
        Assert.Equal("看", CjkBigramProjection.Project("看"));
        Assert.Equal("大模 模型 型上 上线", CjkBigramProjection.Project("大模型上线"));
        Assert.Equal("模型 型评 评估 估流 流程", CjkBigramProjection.Project("模型评估流程"));
        Assert.Equal("下午 午三 三点 点开 开会 。", CjkBigramProjection.Project("下午三点开会。"));
        Assert.Equal("AI 玩具", CjkBigramProjection.Project("AI玩具"));
        Assert.Equal("新款 AI 玩具 具开 开卖 卖了", CjkBigramProjection.Project("新款AI玩具开卖了"));
        Assert.Equal("GPT-5 API", CjkBigramProjection.Project("GPT-5 API"));
        Assert.Equal("200.00", CjkBigramProjection.Project("200.00"));
        Assert.Equal("200.00 元", CjkBigramProjection.Project("200.00元"));
        Assert.Equal("华东 东中 中心 心项 项目 目汇 汇报 V8.pptx", CjkBigramProjection.Project("华东中心项目汇报V8.pptx"));
        Assert.Equal("看 😀 模型", CjkBigramProjection.Project("看😀模型"));
        // Supplementary-plane ideographs are outside the BMP ranges and are not bigram-augmented.
        Assert.Equal("㐀㐁", CjkBigramProjection.Project("㐀㐁"));
        Assert.Equal("ｱｲｳ", CjkBigramProjection.Project("ｱｲｳ"));
        Assert.Equal(
            CjkBigramProjection.Project("新款AI玩具开卖了，快看"),
            CjkBigramProjection.Project("新款AI玩具开卖了，快看"));
    }

    [Fact]
    public void SearchableTextUsesTheSectionThreeFieldInventoryInOrder()
    {
        // semantic_text first, then the indexed payload fields, one per line, in the listed order.
        var file = Message("m1") with
        {
            Type = CanonicalMessageType.File,
            Text = "[文件] x",
            Payload = new JsonObject { ["filename"] = "华东中心项目汇报V8.pptx", ["extension"] = ".pptx", ["size_bytes"] = 12 },
        };
        Assert.Equal("[文件] x\n华东中心项目汇报V8.pptx", SearchableMessageText.Build(file));

        var link = Message("m2") with
        {
            Type = CanonicalMessageType.Link,
            Text = "[链接] title",
            Payload = new JsonObject
            {
                ["title"] = "人工智能产业发展报告",
                ["description"] = "覆盖大模型与芯片产业",
                ["original_url"] = "https://example.com/ai-report",
                ["source_app"] = null,
            },
        };
        Assert.Equal(
            "[链接] title\n人工智能产业发展报告\n覆盖大模型与芯片产业",
            SearchableMessageText.Build(link));

        // forward_bundle: title, then items[].text in stored item order; sender_name is a mutable
        // name and is never indexed (docs/SEARCH_SPEC.md section 3).
        var bundle = Message("m3") with
        {
            Type = CanonicalMessageType.ForwardBundle,
            Payload = new JsonObject
            {
                ["title"] = "项目讨论聊天记录",
                ["item_count"] = 2,
                ["items"] = new JsonArray(
                    new JsonObject { ["sender_name"] = "张三", ["text"] = "第一版价格是不是太高了？" },
                    new JsonObject { ["text"] = "周五前给结论" }),
            },
        };
        Assert.Equal(
            "merge me\n项目讨论聊天记录\n第一版价格是不是太高了？\n周五前给结论",
            SearchableMessageText.Build(bundle with { Text = "merge me" }));

        // Types without an indexed payload field contribute semantic text only.
        Assert.Equal("[图片]", SearchableMessageText.Build(Message("m4") with { Type = CanonicalMessageType.Image, Text = "[图片]" }));

        // A null payload contributes nothing beyond semantic text.
        Assert.Equal("plain", SearchableMessageText.Build(Message("m5") with { Text = "plain", Payload = null }));
    }

    // --------------------------------------------------------------------------------------------
    // Query grammar and compilation
    // --------------------------------------------------------------------------------------------

    [Fact]
    public void CompilerParsesTermsPhrasesPrefixesAndInjectionAttemptsLiterally()
    {
        var andTerms = SearchQueryCompiler.Parse("开会 预算");
        Assert.Equal(2, andTerms.Count);
        Assert.False(andTerms[0].IsPhrase);
        Assert.Equal("开会", andTerms[0].Content);

        var phrase = Assert.Single(SearchQueryCompiler.Parse("\"三点开会\""));
        Assert.True(phrase.IsPhrase);
        Assert.Equal("三点开会", phrase.Content);

        var prefix = Assert.Single(SearchQueryCompiler.Parse("model*"));
        Assert.True(prefix.HasPrefixStar);
        Assert.Equal("model", prefix.Content);

        var spacedPhrase = Assert.Single(SearchQueryCompiler.Parse("\"AI 玩具\""));
        Assert.True(spacedPhrase.IsPhrase);
        Assert.Equal("AI 玩具", spacedPhrase.Content);

        // Caller input can never become FTS5 query syntax: the term stays one literal token
        // sequence and the compiler quotes it (docs/SEARCH_SPEC.md section 5.2).
        var injection = Assert.Single(SearchQueryCompiler.Parse("模型\") OR (1=1) --"));
        Assert.False(injection.IsPhrase);
        Assert.Equal("模型\") OR (1=1) --", injection.Content);
        Assert.Contains("\"", SearchQueryCompiler.Compile([injection]));

        var unbalanced = Assert.Single(SearchQueryCompiler.Parse("\"三点开会"));
        Assert.Equal("\"三点开会", unbalanced.Content);
    }

    [Fact]
    public void CompilerRejectsInvalidQueriesBeforeAnyIndexAccess()
    {
        foreach (var invalid in new[] { string.Empty, "   \t ", "*", "!!!", "开会  !!!" })
        {
            var exception = Assert.Throws<ArchiveQueryException>(() => SearchQueryCompiler.Parse(invalid));
            Assert.Equal(ArchiveQueryErrorCodes.QueryInvalid, exception.Code);
            Assert.Equal(ArchiveQueryFailureKind.Validation, exception.Kind);
        }

        // The failure message never echoes the query content (docs/SEARCH_SPEC.md section 5.3).
        var thrown = Assert.Throws<ArchiveQueryException>(() => SearchQueryCompiler.Parse("!!!"));
        Assert.DoesNotContain("!!!", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilerProjectsTermsExactlyLikeTheIndexedText()
    {
        Assert.Equal("\"大模 模型\"", SearchQueryCompiler.CompileQuery("大模型"));
        Assert.Equal("\"model\"*", SearchQueryCompiler.CompileQuery("model*"));
        Assert.Equal("\"AI 玩具\"", SearchQueryCompiler.CompileQuery("AI玩具"));
        Assert.Equal("\"开会\" \"预算\"", SearchQueryCompiler.CompileQuery("开会 预算"));
        // A phrase term is projected exactly like the indexed text, so a multi-character CJK
        // phrase keeps its exact substring semantics (docs/SEARCH_SPEC.md section 5.2).
        Assert.Equal("\"三点 点开 开会\"", SearchQueryCompiler.CompileQuery("\"三点开会\""));
    }

    // --------------------------------------------------------------------------------------------
    // The #101 recall baseline through the real archive
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task CorpusFixturesPassTheRecallBaselineOfIssue101()
    {
        using var temp = new TempDirectory();
        using var harness = await SearchHarness.CreateAsync(temp.Combine("corpus.db"));

        foreach (var probe in SearchBenchmarkCorpus.ProbeQueries)
        {
            var page = await harness.Service.SearchAsync(
                new SearchRequest { Query = probe.Query, Limit = 500 },
                CancellationToken.None);

            Assert.True(
                page.Items.Select(message => message.Id).ToHashSet(StringComparer.Ordinal).SetEquals(probe.Relevant),
                $"{probe.QueryId} ('{probe.Query}'): expected [{string.Join(", ", probe.Relevant)}] " +
                $"got [{string.Join(", ", page.Items.Select(message => message.Id))}]");
        }
    }

    [Fact]
    public async Task ResultOrderIsBm25WithTheCanonicalTimelineTieBreak()
    {
        using var temp = new TempDirectory();
        using var harness = await SearchHarness.CreateAsync(temp.Combine("corpus.db"));

        // "morning"-like tie: both replica messages of one seed share their rank in their archive;
        // here the harness corpus has unique texts, so build ties through the seeded harness.
        using var tieTemp = new TempDirectory();
        using var tie = new ArchiveQueryHarness();
        await tie.SeedAsync();

        var page = await tie.Service.SearchAsync(
            new SearchRequest { Query = "morning" },
            CancellationToken.None);

        // FirstId and SecondId share the instant and differ only by order key; the deterministic
        // total order is (rank, occurred_utc, source_order_key, id).
        Assert.Equal(
            new[] { ArchiveQueryHarness.FirstId, ArchiveQueryHarness.SecondId },
            page.Items.Select(message => message.Id).ToArray());

        // The contract exposes no score: only the order is part of it.
        Assert.All(page.Items, message => Assert.IsType<CanonicalMessage>(message));
    }

    [Fact]
    public async Task HitsCarryTheFullCanonicalDtoIncludingProvenance()
    {
        using var temp = new TempDirectory();
        using var harness = await SearchHarness.CreateAsync(temp.Combine("corpus.db"));

        var page = await harness.Service.SearchAsync(
            new SearchRequest { Query = "汇报", Limit = 500 },
            CancellationToken.None);

        var hit = Assert.Single(page.Items, message => message.Id == "file_report");
        Assert.Equal(CanonicalMessageType.File, hit.Type);
        Assert.NotNull(hit.Payload);
        Assert.False(string.IsNullOrEmpty(hit.Source.SourceMessageId));
        Assert.False(string.IsNullOrEmpty(hit.Source.AdapterName));
    }

    // --------------------------------------------------------------------------------------------
    // Filters and scoping
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConversationScopeNarrowsResultsToThatConversation()
    {
        using var temp = new TempDirectory();
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var group = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            ConversationId = ArchiveQueryHarness.GroupConversationId,
        }, CancellationToken.None);
        Assert.All(group.Items, message => Assert.Equal(ArchiveQueryHarness.GroupConversationId, message.ConversationId));

        var other = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "other conversation",
            ConversationId = ArchiveQueryHarness.SecondConversationId,
        }, CancellationToken.None);
        Assert.NotEmpty(other.Items);
        Assert.All(other.Items, message => Assert.Equal(ArchiveQueryHarness.SecondConversationId, message.ConversationId));
    }

    [Fact]
    public async Task UnknownConversationIsADeterministicNotFound()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                ConversationId = "g_0000000000000fff",
            }, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.ConversationNotFound, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.NotFound, exception.Kind);
    }

    [Fact]
    public async Task CollectionScopeResolvesThroughTheOneAuthoritativeCatalog()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = CollectionCatalog.SupportedSchemaVersion,
            Collections = new Dictionary<string, CollectionEntryDocument>(StringComparer.Ordinal)
            {
                ["alpha"] = new() { Conversations = [ArchiveQueryHarness.GroupConversationId] },
            },
        };

        var page = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            CollectionName = "alpha",
        }, CancellationToken.None);
        Assert.All(page.Items, message => Assert.Equal(ArchiveQueryHarness.GroupConversationId, message.ConversationId));

        // A member that resolves to no archived conversation contributes no results and never
        // errors by itself.
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = CollectionCatalog.SupportedSchemaVersion,
            Collections = new Dictionary<string, CollectionEntryDocument>(StringComparer.Ordinal)
            {
                ["mixed"] = new() { Conversations = ["g_0000000000000abc", ArchiveQueryHarness.GroupConversationId] },
            },
        };
        var mixed = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            CollectionName = "mixed",
        }, CancellationToken.None);
        Assert.All(mixed.Items, message => Assert.Equal(ArchiveQueryHarness.GroupConversationId, message.ConversationId));
    }

    [Fact]
    public async Task UnknownCollectionAndInvalidConfigurationAreDeterministicFailures()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var notFound = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                CollectionName = "missing",
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CollectionNotFound, notFound.Code);
        Assert.Equal(ArchiveQueryFailureKind.NotFound, notFound.Kind);

        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = "9.9",
            Collections = [],
        };
        var configInvalid = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                CollectionName = "alpha",
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CollectionConfigInvalid, configInvalid.Code);
        Assert.Equal(ArchiveQueryFailureKind.Validation, configInvalid.Kind);
    }

    [Fact]
    public async Task ConversationAndCollectionScopesAreMutuallyExclusive()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                ConversationId = ArchiveQueryHarness.GroupConversationId,
                CollectionName = "alpha",
            }, CancellationToken.None));

        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task ParticipantTypeAndTimeFiltersReuseTheMessageListSemantics()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var alice = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            ParticipantId = ArchiveQueryHarness.AliceId,
        }, CancellationToken.None);
        Assert.Equal([ArchiveQueryHarness.FirstId], alice.Items.Select(message => message.Id).ToArray());

        // A well-formed but unknown participant is not an error: the filter matches nothing and
        // returns an empty successful page (docs/SEARCH_SPEC.md section 12).
        var unknownParticipant = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            ParticipantId = "u_00000000000000ff",
        }, CancellationToken.None);
        Assert.Empty(unknownParticipant.Items);
        Assert.False(unknownParticipant.HasMore);
        Assert.Null(unknownParticipant.NextCursor);

        var malformed = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                ParticipantId = "alice",
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, malformed.Code);

        var unknownType = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "未识别",
            Type = "unknown",
        }, CancellationToken.None);
        Assert.All(unknownType.Items, message => Assert.Equal(CanonicalMessageType.Unknown, message.Type));

        var unknownTypeMismatch = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "未识别",
            Type = "text",
        }, CancellationToken.None);
        Assert.Empty(unknownTypeMismatch.Items);

        var badType = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest { Query = "morning", Type = "nonsense" }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, badType.Code);

        var bounded = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            Since = "2026-01-02",
            Until = "2026-01-05",
        }, CancellationToken.None);
        Assert.Empty(bounded.Items);

        var unbounded = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            Since = "2026-01-01",
        }, CancellationToken.None);
        Assert.Equal(2, unbounded.Items.Count);

        var badRange = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                Since = "2026-01-05",
                Until = "2026-01-01",
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, badRange.Code);
    }

    // --------------------------------------------------------------------------------------------
    // Pagination
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task PaginationIsKeysetBoundAndPageSizeIndependent()
    {
        using var temp = new TempDirectory();
        using var harness = await SearchHarness.CreateAsync(temp.Combine("corpus.db"));

        // Walk the whole "模型" match set one row at a time, then resume with a growing page size;
        // both walks must yield the identical deterministic order without duplicates.
        var expected = await harness.Service.SearchAsync(
            new SearchRequest { Query = "模型", Limit = 500 }, CancellationToken.None);
        Assert.True(expected.Items.Count > 2);

        var stepped = new List<string>();
        string? cursor = null;
        do
        {
            var page = await harness.Service.SearchAsync(new SearchRequest
            {
                Query = "模型",
                Limit = 1,
                Cursor = cursor,
            }, CancellationToken.None);
            stepped.AddRange(page.Items.Select(message => message.Id));
            Assert.True(page.Items.Count <= 1);
            Assert.Equal(page.HasMore, page.NextCursor is not null);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(expected.Items.Select(message => message.Id), stepped);

        // A caller may change --limit between pages (page size is excluded from the cursor).
        var first = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "模型",
            Limit = 1,
        }, CancellationToken.None);
        var second = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "模型",
            Limit = 500,
            Cursor = first.NextCursor,
        }, CancellationToken.None);
        Assert.Equal(
            expected.Items.Skip(1).Select(message => message.Id),
            second.Items.Select(message => message.Id));
    }

    [Fact]
    public async Task CursorIsBoundToTheQueryAndFilterSet()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var first = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            Limit = 1,
        }, CancellationToken.None);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var mismatchedQuery = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "tie",
                Cursor = first.NextCursor,
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, mismatchedQuery.Code);

        var mismatchedFilter = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest
            {
                Query = "morning",
                ParticipantId = ArchiveQueryHarness.AliceId,
                Cursor = first.NextCursor,
            }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.CursorInvalid, mismatchedFilter.Code);

        // Whitespace spelling differences are the same normalized query and share cursors.
        var respelled = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "  morning  ",
            Limit = 1,
            Cursor = first.NextCursor,
        }, CancellationToken.None);
        Assert.False(respelled.HasMore);
    }

    [Fact]
    public async Task LimitBoundsMatchTheMessageListContract()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        foreach (var invalidLimit in new[] { 0, -1, 501 })
        {
            var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
                () => harness.Service.SearchAsync(new SearchRequest
                {
                    Query = "morning",
                    Limit = invalidLimit,
                }, CancellationToken.None));
            Assert.Equal(ArchiveQueryErrorCodes.InvalidRequest, exception.Code);
        }

        var minimal = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "morning",
            Limit = 1,
        }, CancellationToken.None);
        Assert.Single(minimal.Items);
        Assert.True(minimal.HasMore);
    }

    // --------------------------------------------------------------------------------------------
    // Failure modes
    // --------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidQueriesFailClosedWithoutReachingTheIndex()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        foreach (var invalid in new[] { string.Empty, "   ", "*", "!!!" })
        {
            var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
                () => harness.Service.SearchAsync(new SearchRequest { Query = invalid }, CancellationToken.None));
            Assert.Equal(ArchiveQueryErrorCodes.QueryInvalid, exception.Code);
        }
    }

    [Fact]
    public async Task NoMatchQueriesAreSuccessfulEmptyResults()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();

        var noMatch = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "不存在的词组",
        }, CancellationToken.None);
        Assert.Empty(noMatch.Items);
        Assert.False(noMatch.HasMore);
        Assert.Null(noMatch.NextCursor);

        // FTS5 operator-looking input is one literal token sequence that matches nothing.
        var injection = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "模型\") OR (1=1) --",
        }, CancellationToken.None);
        Assert.Empty(injection.Items);
        Assert.False(injection.HasMore);
    }

    [Fact]
    public async Task EmptyArchiveSearchesAsAnEmptySuccess()
    {
        using var temp = new TempDirectory();
        var store = new SqliteArchiveStore(temp.Combine("empty.db"), new FixedClock());
        await store.InitializeAsync(CancellationToken.None);
        using var harness = new SearchHarness(store, new CollectionCatalogService(new StubCollectionCatalogSource()));

        var page = await harness.Service.SearchAsync(new SearchRequest
        {
            Query = "模型",
        }, CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task MissingIndexFailsClosedWithSearchIndexUnavailable()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        await harness.DropSearchIndexAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest { Query = "morning" }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.SearchIndexUnavailable, exception.Code);
        Assert.Equal(ArchiveQueryFailureKind.Unavailable, exception.Kind);
    }

    [Fact]
    public async Task DeletedIndexRowsAreNotSilentlyPresentedAsResults()
    {
        using var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        await harness.DeleteAllIndexRowsAsync();

        var exception = await Assert.ThrowsAsync<ArchiveQueryException>(
            () => harness.Service.SearchAsync(new SearchRequest { Query = "morning" }, CancellationToken.None));
        Assert.Equal(ArchiveQueryErrorCodes.SearchIndexUnavailable, exception.Code);
    }

    // --------------------------------------------------------------------------------------------
    // Harness
    // --------------------------------------------------------------------------------------------

    /// <summary>
    /// A search harness over one real archive plus the service boundary; the archive is seeded
    /// either with the benchmark corpus fixtures or left empty.
    /// </summary>
    internal sealed class SearchHarness : IDisposable
    {
        private readonly TempDirectory? _temp;
        private readonly SqliteArchiveStore _store;

        public SearchHarness(SqliteArchiveStore store, CollectionCatalogService collections, TempDirectory? temp = null)
        {
            _store = store;
            _temp = temp;
            Service = new ArchiveQueryService(
                store,
                new FakeRawVaultStore(),
                new RawVaultIngestService(new ThrowingRawVaultStore(), store, new FixedClock()),
                collections,
                new FixedClock());
        }

        public ArchiveQueryService Service { get; }

        public static async Task<SearchHarness> CreateAsync(string archivePath)
        {
            var store = await SearchIndexFixture.CreateSeededArchiveAsync(archivePath);
            return new SearchHarness(store, new CollectionCatalogService(new StubCollectionCatalogSource()), null);
        }

        public void Dispose()
        {
            _temp?.Dispose();
        }
    }

    private static CanonicalMessage Message(string id) => new()
    {
        Id = id,
        ConversationId = "g_00000000000000c1",
        SenderId = "u_00000000000000a1",
        OccurredAt = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.FromHours(8)),
        Type = CanonicalMessageType.Text,
        Text = "merge me",
        Source = new SourceProvenance
        {
            SourceProfileId = "wxid_fixture",
            SourceConversationId = "source-1",
            SourceMessageId = $"src:{id}",
            AdapterName = "wechat-windows",
        },
    };
}
