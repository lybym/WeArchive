using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for <c>wearchive search</c> ([Issue #103](
/// https://github.com/lybym/WeArchive/issues/103), M3b-2, docs/SEARCH_SPEC.md sections 9.2 and 12,
/// docs/CLI.md): the pinned JSON page shape, human output, stdout/stderr separation, documented
/// exit codes, the section 12 error mapping, cursor pagination, the query/filters surface and the
/// end-to-end Chinese/mixed-language match → canonical ID → <c>context</c> → provenance chain.
/// <para>
/// Like <see cref="ArchiveQueryCliTests"/> the provider registers a source adapter that fails on
/// every call, so a passing test also proves that search reads only the canonical archive and
/// never contacts live WeChat or Raw Vault. Query semantics themselves are owned by
/// <see cref="ArchiveQueryService"/> and pinned by <see cref="ArchiveQueryServiceSearchTests"/>;
/// these tests prove the transport neither duplicates nor weakens them.
/// </para>
/// </summary>
public sealed class ArchiveSearchCliTests
{
    private const string MixedLanguageId = "m_0000000000000201";
    private const string MixedLanguageText = "新款AI玩具开卖了";

    private static ServiceProvider BuildProvider(ArchiveQueryHarness harness)
    {
        var services = new ServiceCollection();
        // Pre-register the stub Collection catalog so AddWeArchiveCore's TryAddSingleton keeps it
        // and the CLI exercises the same authoritative-catalog resolution the service documents.
        services.AddSingleton<ICollectionCatalogSource>(harness.CollectionSource);
        services.AddWeArchiveCore(harness.ArchivePath, harness.VaultRoot);
        services.AddSingleton<IClock>(harness.Clock);
        services.AddSingleton<IRawVaultStore>(harness.Vault);
        services.AddSingleton<ISourceAdapter>(new ThrowingSourceAdapter());
        return services.BuildServiceProvider();
    }

    private static async Task<ArchiveQueryHarness> SeededAsync()
    {
        var harness = new ArchiveQueryHarness();
        await harness.SeedAsync();
        return harness;
    }

    /// <summary>
    /// Adds one mixed Chinese/Latin message through the real store write path, so the derived
    /// index holds it exactly as a published conversation would (docs/DEVELOPMENT.md section 7).
    /// </summary>
    private static async Task<CanonicalMessage> SeedMixedLanguageAsync(ArchiveQueryHarness harness)
    {
        var message = ArchiveQueryHarness.Message(
            MixedLanguageId,
            ArchiveQueryHarness.GroupConversationId,
            ArchiveQueryHarness.At(1, 6),
            ArchiveQueryHarness.AliceId,
            CanonicalMessageType.Text,
            MixedLanguageText,
            "0001",
            "l:message_0:20");
        await harness.Store.UpsertMessagesAsync([message], CancellationToken.None);
        return message;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        ServiceProvider provider,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(args, stdout, stderr, provider, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument ParseSingleDocument(string stdout)
    {
        var output = stdout.TrimEnd();
        Assert.False(output.Contains('\n'), $"stdout must carry exactly one JSON document, got: {output}");

        return JsonDocument.Parse(output);
    }

    // ---- search: JSON success ----------------------------------------------

    [Fact]
    public async Task SearchJsonEmitsThePinnedPageShape()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "search", "morning", "--json");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);

        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal(
            ["has_more", "items", "next_cursor"],
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_cursor").ValueKind);
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(
            new[] { ArchiveQueryHarness.FirstId, ArchiveQueryHarness.SecondId }.OrderBy(id => id, StringComparer.Ordinal),
            root.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())
                .OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SearchItemsAreByteShapedLikeMessageListItems()
    {
        // docs/SEARCH_SPEC.md section 9.2: every search item is byte-shaped like the
        // `message list` item DTO, so a Harness chains search → context without a second
        // representation. Same message, same serialization, identical raw JSON.
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, listOut, _) = await RunAsync(
            provider, "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");
        var (_, searchOut, _) = await RunAsync(provider, "search", "morning", "--json");

        using var list = ParseSingleDocument(listOut);
        using var search = ParseSingleDocument(searchOut);
        var listItem = list.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ArchiveQueryHarness.FirstId);
        var searchItem = search.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ArchiveQueryHarness.FirstId);

        Assert.Equal(listItem.GetRawText(), searchItem.GetRawText());
        Assert.Equal(
            ["conversation_id", "id", "is_partial", "occurred_at", "payload", "reply_to", "sender_id", "source", "text", "type"],
            searchItem.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task MixedLanguageMatchChainsSearchToContextAndProvenance()
    {
        // Issue #103 acceptance: end-to-end Chinese/mixed-language match → canonical ID →
        // context → provenance, all through the CLI surface.
        using var harness = await SeededAsync();
        await SeedMixedLanguageAsync(harness);
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "AI玩具", "--json");
        Assert.Equal(ExitCode.Success, exit);

        using (var search = ParseSingleDocument(stdout))
        {
            var hits = search.RootElement.GetProperty("items");
            Assert.Equal(1, hits.GetArrayLength());
            var hit = hits[0];
            Assert.Equal(MixedLanguageId, hit.GetProperty("id").GetString());
            Assert.Equal(MixedLanguageText, hit.GetProperty("text").GetString());
            Assert.Equal(ArchiveQueryHarness.GroupConversationId, hit.GetProperty("conversation_id").GetString());

            // The full canonical provenance block travels with the hit (MESSAGE_SCHEMA 3.4).
            var source = hit.GetProperty("source");
            Assert.Equal("l:message_0:20", source.GetProperty("source_message_id").GetString());
            Assert.Equal("message_0", source.GetProperty("source_partition").GetString());
            Assert.Equal("0001", source.GetProperty("source_order_key").GetString());
        }

        var (contextExit, contextOut, _) = await RunAsync(provider, "context", MixedLanguageId, "--json");
        Assert.Equal(ExitCode.Success, contextExit);

        using var context = ParseSingleDocument(contextOut);
        Assert.Equal(MixedLanguageId, context.RootElement.GetProperty("message_id").GetString());
        Assert.Equal(
            MixedLanguageId,
            context.RootElement.GetProperty("message").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ChineseQueryMatchesInsideLongerRunsThroughTheCli()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        // 2-character CJK substring semantics through the transport: 未识别 ⊂ [未识别消息].
        var (exit, stdout, _) = await RunAsync(provider, "search", "未识别", "--json");
        Assert.Equal(ExitCode.Success, exit);

        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            new[] { ArchiveQueryHarness.UnknownId },
            document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task NoMatchIsASuccessfulEmptyPage()
    {
        // docs/SEARCH_SPEC.md section 5.4: zero matches is a successful result, never a failure.
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "nonexistent-word", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(document.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task OperatorLookingInputIsALiteralTermThatMatchesNothing()
    {
        // docs/SEARCH_SPEC.md section 12: FTS5 operator-looking input is a literal token
        // sequence — no injection surface, exit 0 with empty results.
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "模型\") OR (1=1) --", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
    }

    // ---- search: human output and stream discipline ------------------------

    [Fact]
    public async Task SearchHumanOutputLeadsWithTheStableIdAndCursorLine()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "search", "morning", "--limit", "1");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Hits (1) [more available]:", stdout, StringComparison.Ordinal);
        Assert.Contains(ArchiveQueryHarness.FirstId, stdout, StringComparison.Ordinal);
        Assert.Contains("next cursor: ", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("{", stdout, StringComparison.Ordinal);
        Assert.Contains("Searching canonical messages", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchJsonCarriesNoProgressOrAnsiDecoration()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, stdout, stderr) = await RunAsync(provider, "search", "morning", "--json");

        Assert.DoesNotContain('\u001b', stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task SearchQuietSuppressesProgressButNotTheResult()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "search", "morning", "--quiet");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);
        Assert.Contains("Hits (", stdout, StringComparison.Ordinal);
    }

    // ---- search: pagination -------------------------------------------------

    [Fact]
    public async Task SearchJsonPagesWithAnOpaqueCursorWithoutDuplicates()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (firstExit, firstOut, _) = await RunAsync(provider, "search", "morning", "--limit", "1", "--json");
        Assert.Equal(ExitCode.Success, firstExit);

        using var first = ParseSingleDocument(firstOut);
        Assert.True(first.RootElement.GetProperty("has_more").GetBoolean());
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        Assert.DoesNotContain(ArchiveQueryHarness.GroupConversationId, cursor!, StringComparison.Ordinal);

        var (secondExit, secondOut, _) = await RunAsync(
            provider, "search", "morning", "--limit", "10", "--cursor", cursor!, "--json");
        Assert.Equal(ExitCode.Success, secondExit);

        using var second = ParseSingleDocument(secondOut);
        Assert.False(second.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("next_cursor").ValueKind);

        var ids = first.RootElement.GetProperty("items").EnumerateArray()
            .Concat(second.RootElement.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("id").GetString())
            .ToList();
        Assert.Equal(
            new[] { ArchiveQueryHarness.FirstId, ArchiveQueryHarness.SecondId }.OrderBy(id => id, StringComparer.Ordinal),
            ids.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task SearchCursorIsBoundToTheQuery()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, firstOut, _) = await RunAsync(provider, "search", "morning", "--limit", "1", "--json");
        using var first = ParseSingleDocument(firstOut);
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();

        var (exit, stdout, _) = await RunAsync(provider, "search", "tie", "--cursor", cursor!, "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("cursor_invalid", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- search: filters ----------------------------------------------------

    [Fact]
    public async Task SearchAppliesConversationParticipantTypeAndTimeFilters()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (participantExit, participantOut, _) = await RunAsync(
            provider, "search", "morning", "--participant", ArchiveQueryHarness.AliceId, "--json");
        Assert.Equal(ExitCode.Success, participantExit);
        using (var participant = ParseSingleDocument(participantOut))
        {
            Assert.Equal(
                [ArchiveQueryHarness.FirstId],
                participant.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("id").GetString()));
        }

        var (typeExit, typeOut, _) = await RunAsync(
            provider, "search", "未识别", "--type", "unknown", "--json");
        Assert.Equal(ExitCode.Success, typeExit);
        using (var type = ParseSingleDocument(typeOut))
        {
            Assert.Equal(
                [ArchiveQueryHarness.UnknownId],
                type.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("id").GetString()));
        }

        var (scopeExit, scopeOut, _) = await RunAsync(
            provider, "search", "morning", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");
        Assert.Equal(ExitCode.Success, scopeExit);
        using (var scoped = ParseSingleDocument(scopeOut))
        {
            Assert.Equal(2, scoped.RootElement.GetProperty("items").GetArrayLength());
        }

        // An inclusive time bound identical to `message list`: the morning messages are on 1 Jan.
        var (sinceExit, sinceOut, _) = await RunAsync(
            provider, "search", "morning", "--since", "2026-01-02", "--json");
        Assert.Equal(ExitCode.Success, sinceExit);
        using (var since = ParseSingleDocument(sinceOut))
        {
            Assert.Equal(0, since.RootElement.GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task SearchCollectionScopeUnionsMemberConversations()
    {
        using var harness = await SeededAsync();
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = CollectionCatalog.SupportedSchemaVersion,
            Collections = new Dictionary<string, CollectionEntryDocument>(StringComparer.Ordinal)
            {
                ["alpha"] = new()
                {
                    Conversations = [ArchiveQueryHarness.GroupConversationId, ArchiveQueryHarness.SecondConversationId],
                },
            },
        };
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "report", "--collection", "alpha", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            new[] { "m_0000000000000102" },
            document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task UnknownCollectionFailsAsCollectionNotFound()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--collection", "missing", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("collection_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidCollectionConfigFailsAsCollectionConfigInvalid()
    {
        using var harness = await SeededAsync();
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = "9.9",
            Collections = [],
        };
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--collection", "alpha", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            "collection_config_invalid",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- search: the SEARCH_SPEC section 12 error mapping -------------------

    [Fact]
    public async Task UnknownConversationFailsAsConversationNotFound()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider, "search", "morning", "--conversation", "g_0000000000000fff", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            "conversation_not_found",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedParticipantIsAUsageError()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--participant", "alice", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownWellFormedParticipantIsAnEmptySuccessfulPage()
    {
        // docs/SEARCH_SPEC.md section 12: not an error — same as `message list`, the filter
        // matches nothing and returns an empty successful page.
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider, "search", "morning", "--participant", "u_00000000000000ff", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(0, document.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(document.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task ConversationAndCollectionScopesAreMutuallyExclusive()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider,
            "search", "morning",
            "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--collection", "alpha",
            "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("!!!")]
    public async Task InvalidQueriesFailAsQueryInvalidWithoutEchoingContent(string query)
    {
        // docs/SEARCH_SPEC.md sections 5.3 and 12: a deterministic usage failure before any
        // FTS5 call, and the message never echoes the query content.
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", query, "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("query_invalid", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        if (!string.IsNullOrWhiteSpace(query))
        {
            // The failure document never echoes the query content back
            // (docs/SEARCH_SPEC.md sections 5.3 and 12).
            Assert.DoesNotContain(query, stdout, StringComparison.Ordinal);
        }
    }

    // ---- search: index and archive failures ---------------------------------

    [Fact]
    public async Task MissingIndexFailsClosedAsSearchIndexUnavailable()
    {
        using var harness = await SeededAsync();
        await harness.DropSearchIndexAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            "search_index_unavailable",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DeletedIndexRowsFailClosedAsSearchIndexUnavailable()
    {
        using var harness = await SeededAsync();
        await harness.DeleteAllIndexRowsAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            "search_index_unavailable",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnreadableArchiveIsArchiveUnavailable()
    {
        using var harness = await SeededAsync();
        await File.WriteAllTextAsync(harness.ArchivePath, "this is not a SQLite database");
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            "archive_unavailable",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- search: usage parsing ----------------------------------------------

    [Fact]
    public async Task MissingQueryIsAUsageFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ExtraPositionalArgumentIsAUsageFailure()
    {
        // The documented synopsis is `wearchive search <query>`: a multi-term query is quoted as
        // one argument, and extra positionals are refused instead of being silently concatenated.
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "开会", "预算", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownOptionIsAUsageFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", "--bogus", "1", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "501")]
    [InlineData("--limit", "many")]
    [InlineData("--since", "yesterday")]
    [InlineData("--type", "quote")]
    public async Task InvalidOptionValuesAreUsageFailures(string option, string value)
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "search", "morning", option, value, "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- automation safety ---------------------------------------------------

    [Fact]
    public async Task NoInputNeverPromptsAndStillSucceeds()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider, "search", "morning", "--json", "--no-input");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(2, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task CancellationExitsWithTheDocumentedCancellationCode()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(
            ["search", "morning", "--json"], stdout, stderr, provider, cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        using var document = ParseSingleDocument(stdout.ToString());
        Assert.Equal("cancelled", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SearchIsAdvertisedByHelp()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "--help", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var commands = document.RootElement.GetProperty("commands").EnumerateArray()
            .Select(command => command.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("search", commands);
    }
}
