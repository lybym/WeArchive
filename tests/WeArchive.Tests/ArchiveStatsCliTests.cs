using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for <c>wearchive stats</c> (issue #104, M3c, docs/CLI.md,
/// docs/DATA_MODEL.md section 24): the pinned JSON document shape, human output, stdout/stderr
/// separation, documented exit codes, breakdown pagination through the transport and the scope
/// surface.
/// <para>
/// Like <see cref="ArchiveSearchCliTests"/> the provider registers a source adapter that fails on
/// every call, so a passing test also proves that statistics read only the canonical archive and
/// never contact live WeChat or Raw Vault. Statistics semantics themselves are owned by
/// <see cref="ArchiveQueryService"/> and pinned by <see cref="ArchiveStatisticsServiceTests"/>.
/// </para>
/// </summary>
public sealed class ArchiveStatsCliTests
{
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

    // ---- JSON success -------------------------------------------------------

    [Fact]
    public async Task StatsJsonEmitsThePinnedDocumentShape()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "stats", "--json");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);

        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal(
            ["basis", "breakdown", "by_type", "scope", "totals"],
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        var scope = root.GetProperty("scope");
        Assert.Equal(
            ["collection", "conversation_id", "kind", "since", "until"],
            scope.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("archive", scope.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("conversation_id").ValueKind);

        var basis = root.GetProperty("basis");
        Assert.Equal("canonical_archive", basis.GetProperty("source").GetString());
        Assert.Equal(harness.ArchivePath, basis.GetProperty("archive_path").GetString());
        Assert.Equal("2026-03-01T12:00:00+08:00", basis.GetProperty("queried_at").GetString());

        var totals = root.GetProperty("totals");
        Assert.Equal(12, totals.GetProperty("message_count").GetInt32());
        Assert.Equal(2, totals.GetProperty("conversation_count").GetInt32());
        Assert.Equal(2, totals.GetProperty("sender_participant_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("unattributed_message_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("unknown_count").GetInt32());
        Assert.Equal(1, totals.GetProperty("partial_count").GetInt32());
        Assert.Equal("2026-01-01T09:00:00+08:00", totals.GetProperty("first_message_at").GetString());
        Assert.Equal("2026-02-02T09:00:00+08:00", totals.GetProperty("last_message_at").GetString());

        var byType = root.GetProperty("by_type");
        Assert.Equal(
            new[] { "file", "image", "link", "system", "text", "unknown", "voice" },
            byType.EnumerateArray().Select(type => type.GetProperty("type").GetString()!).ToArray());
        Assert.Equal(
            new[] { 1, 1, 1, 1, 6, 1, 1 },
            byType.EnumerateArray().Select(type => type.GetProperty("count").GetInt32()).ToArray());

        var breakdown = root.GetProperty("breakdown");
        Assert.Equal(
            ["bucket", "has_more", "items", "next_cursor"],
            breakdown.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("day", breakdown.GetProperty("bucket").GetString());
        Assert.False(breakdown.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, breakdown.GetProperty("next_cursor").ValueKind);
        Assert.Equal(7, breakdown.GetProperty("items").GetArrayLength());

        var firstItem = breakdown.GetProperty("items")[0];
        Assert.Equal(
            ["bucket_start", "conversation_id", "message_count", "partial_count", "unknown_count"],
            firstItem.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("2026-01-01T00:00:00+00:00", firstItem.GetProperty("bucket_start").GetString());
        Assert.Equal(3, firstItem.GetProperty("message_count").GetInt32());
        Assert.Equal(1, firstItem.GetProperty("unknown_count").GetInt32());
        Assert.Equal(0, firstItem.GetProperty("partial_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, firstItem.GetProperty("conversation_id").ValueKind);
    }

    [Fact]
    public async Task StatsMonthBucketIsSelectedByTheBucketOption()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "stats", "--bucket", "month", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var breakdown = document.RootElement.GetProperty("breakdown");
        Assert.Equal("month", breakdown.GetProperty("bucket").GetString());
        Assert.Equal(2, breakdown.GetProperty("items").GetArrayLength());
        Assert.Equal(
            ["2026-01-01T00:00:00+00:00", "2026-02-01T00:00:00+00:00"],
            breakdown.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("bucket_start").GetString()!).ToArray());
    }

    [Fact]
    public async Task StatsConversationBreakdownCarriesStableConversationIds()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "stats", "--bucket", "conversation", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var breakdown = document.RootElement.GetProperty("breakdown");
        Assert.Equal("conversation", breakdown.GetProperty("bucket").GetString());
        Assert.Equal(
            [ArchiveQueryHarness.GroupConversationId, ArchiveQueryHarness.SecondConversationId],
            breakdown.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("conversation_id").GetString()!).ToArray());
    }

    [Fact]
    public async Task StatsConversationScopeIsReportedAndApplied()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider, "stats", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal("conversation", root.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(
            ArchiveQueryHarness.GroupConversationId,
            root.GetProperty("scope").GetProperty("conversation_id").GetString());
        Assert.Equal(9, root.GetProperty("totals").GetProperty("message_count").GetInt32());
        Assert.Equal(1, root.GetProperty("totals").GetProperty("unknown_count").GetInt32());
    }

    [Fact]
    public async Task StatsCollectionScopeResolvesThroughTheAuthoritativeCatalog()
    {
        using var harness = await SeededAsync();
        harness.CollectionSource.Document = new CollectionCatalogDocument
        {
            SchemaVersion = "1.0",
            Collections = new()
            {
                ["alpha"] = new CollectionEntryDocument
                {
                    Conversations = [ArchiveQueryHarness.GroupConversationId],
                },
            },
        };
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "stats", "--collection", "alpha", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal("collection", root.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal("alpha", root.GetProperty("scope").GetProperty("collection").GetString());
        Assert.Equal(9, root.GetProperty("totals").GetProperty("message_count").GetInt32());
    }

    [Fact]
    public async Task StatsBreakdownPaginationResumesThroughTheCli()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (firstExit, firstOut, _) = await RunAsync(provider, "stats", "--bucket", "day", "--limit", "2", "--json");
        Assert.Equal(ExitCode.Success, firstExit);
        string firstCursor;
        using (var first = ParseSingleDocument(firstOut))
        {
            var breakdown = first.RootElement.GetProperty("breakdown");
            Assert.Equal(2, breakdown.GetProperty("items").GetArrayLength());
            Assert.True(breakdown.GetProperty("has_more").GetBoolean());
            firstCursor = breakdown.GetProperty("next_cursor").GetString()!;
        }

        var (secondExit, secondOut, _) = await RunAsync(
            provider, "stats", "--bucket", "day", "--limit", "2", "--cursor", firstCursor, "--json");
        Assert.Equal(ExitCode.Success, secondExit);
        using (var second = ParseSingleDocument(secondOut))
        {
            var breakdown = second.RootElement.GetProperty("breakdown");
            Assert.Equal(
                ["2026-01-03T00:00:00+00:00", "2026-01-04T00:00:00+00:00"],
                breakdown.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("bucket_start").GetString()!).ToArray());
            // Scope-wide totals never shrink to the page.
            Assert.Equal(12, second.RootElement.GetProperty("totals").GetProperty("message_count").GetInt32());
        }
    }

    // ---- failure mapping ----------------------------------------------------

    [Fact]
    public async Task UnknownConversationExitsOneWithConversationNotFound()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "stats", "--conversation", "g_0000000000000fff", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            CliErrorCode.ConversationNotFound,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("error:", stderr.ToString());
    }

    [Fact]
    public async Task UnknownCollectionExitsOneWithCollectionNotFound()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "stats", "--collection", "missing", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            CliErrorCode.CollectionNotFound,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnsupportedBucketExitsTwoWithUsageError()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "stats", "--bucket", "week", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            CliErrorCode.UsageError,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownOptionAndUnexpectedPositionalExitTwo()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (unknownOptionExit, unknownOptionOut, _) = await RunAsync(provider, "stats", "--bogus", "1", "--json");
        Assert.Equal(ExitCode.UsageError, unknownOptionExit);
        using (var document = ParseSingleDocument(unknownOptionOut))
        {
            Assert.Equal(
                CliErrorCode.UsageError,
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var (positionalExit, positionalOut, _) = await RunAsync(provider, "stats", "extra", "--json");
        Assert.Equal(ExitCode.UsageError, positionalExit);
        using (var document = ParseSingleDocument(positionalOut))
        {
            Assert.Equal(
                CliErrorCode.UsageError,
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task MalformedCursorExitsTwoWithCursorInvalid()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "stats", "--cursor", "bogus", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            CliErrorCode.CursorInvalid,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- human output, process contract, discovery ---------------------------

    [Fact]
    public async Task StatsHumanOutputSummarizesTotalsAndKeepsJsonOffStdout()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "stats");

        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.ToString();
        Assert.StartsWith("Statistics (whole archive):", output, StringComparison.Ordinal);
        Assert.Contains("messages: 12", output);
        Assert.Contains("unknown: 1", output);
        Assert.Contains("partial: 1", output);
        Assert.Contains("unattributed: 1", output);
        Assert.Contains("By type:", output);
        Assert.Contains("unknown  1", output);
        Assert.Contains("By day (7):", output);
        Assert.DoesNotContain("{", output);
        // Progress belongs on stderr in human mode.
        Assert.Contains("Counting canonical archive records", stderr.ToString());
    }

    [Fact]
    public async Task StatsJsonSuppressesProgressAndNoInputIsAccepted()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "stats", "--json", "--no-input", "--quiet");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);
        Assert.NotEmpty(stdout.ToString());
    }

    [Fact]
    public async Task HelpListsTheStatsCommand()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "--help", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var commands = document.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Select(command => command.GetProperty("name").GetString()!)
            .ToList();
        Assert.Contains("stats", commands);
    }
}
