using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Abstractions;
using WeArchive.Core.RawVault;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for <c>message list</c> and <c>context</c>: option parsing, the pinned JSON
/// shapes, stdout/stderr separation, <c>--quiet</c>/<c>--no-input</c>, documented exit codes and
/// cancellation (docs/DEVELOPMENT.md section 7 "CLI contract tests", docs/CLI.md).
/// <para>
/// The provider deliberately registers a source adapter and a preservation store that fail on
/// every call, so a passing test also proves that canonical retrieval needs neither a live WeChat
/// client nor Raw Vault access.
/// </para>
/// </summary>
public sealed class ArchiveQueryCliTests
{
    private static ServiceProvider BuildProvider(ArchiveQueryHarness harness)
    {
        var services = new ServiceCollection();
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

    // ---- message list: JSON success ----------------------------------------

    [Fact]
    public async Task MessageListJsonEmitsThePinnedPageShape()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);

        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.GetProperty("items").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_cursor").ValueKind);
        Assert.False(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(harness.GroupMessages.Count, root.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task MessageListJsonPinsEveryItemFieldName()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--type", "image", "--json");

        using var document = ParseSingleDocument(stdout);
        var item = document.RootElement.GetProperty("items")[0];

        Assert.Equal(
            ["conversation_id", "id", "is_partial", "occurred_at", "payload", "reply_to", "sender_id", "source", "text", "type"],
            item.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal(
            ["source_message_id", "source_order_key", "source_partition", "source_subtype", "source_type"],
            item.GetProperty("source").EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        Assert.StartsWith("m_", item.GetProperty("id").GetString(), StringComparison.Ordinal);
        Assert.Equal("image", item.GetProperty("type").GetString());
        Assert.Equal("chart.png", item.GetProperty("payload").GetProperty("file_name").GetString());
    }

    [Fact]
    public async Task MessageListJsonExposesCanonicalSemanticsAndNoArchiveInternals()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");

        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;

        var topLevelNames = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.DoesNotContain("occurred_utc", topLevelNames);
        Assert.DoesNotContain("content_hash", topLevelNames);
        Assert.DoesNotContain("semantic_text", topLevelNames);
        Assert.DoesNotContain("payload_json", topLevelNames);
        Assert.DoesNotContain("reply_snapshot_json", topLevelNames);

        var unknown = root.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ArchiveQueryHarness.UnknownId);
        Assert.Equal("unknown", unknown.GetProperty("type").GetString());
        Assert.Equal("[未识别消息]", unknown.GetProperty("text").GetString());

        var system = root.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ArchiveQueryHarness.SystemId);
        Assert.Equal(JsonValueKind.Null, system.GetProperty("sender_id").ValueKind);

        var partial = root.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ArchiveQueryHarness.PartialId);
        Assert.True(partial.GetProperty("is_partial").GetBoolean());

        // A resolved reply exposes the canonical target id; the upstream id stays provenance.
        var reply = root.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == ArchiveQueryHarness.SecondId)
            .GetProperty("reply_to");
        Assert.Equal(ArchiveQueryHarness.FirstId, reply.GetProperty("message_id").GetString());
        Assert.Equal("Alice", reply.GetProperty("sender_name").GetString());
    }

    [Fact]
    public async Task MessageListJsonPagesWithAnOpaqueCursorAndNoDuplicates()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (firstExit, firstOut, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--limit", "2", "--json");
        Assert.Equal(ExitCode.Success, firstExit);

        using var first = ParseSingleDocument(firstOut);
        Assert.True(first.RootElement.GetProperty("has_more").GetBoolean());
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        Assert.DoesNotContain(ArchiveQueryHarness.GroupConversationId, cursor!, StringComparison.Ordinal);

        var (secondExit, secondOut, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--limit", "7", "--cursor", cursor!, "--json");
        Assert.Equal(ExitCode.Success, secondExit);

        using var second = ParseSingleDocument(secondOut);
        Assert.False(second.RootElement.GetProperty("has_more").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("next_cursor").ValueKind);

        var ids = first.RootElement.GetProperty("items").EnumerateArray()
            .Concat(second.RootElement.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("id").GetString())
            .ToList();

        Assert.Equal(
            ArchiveQueryHarness.CanonicalOrder(harness.GroupMessages).Select(message => message.Id),
            ids);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task MessageListJsonAppliesDateParticipantAndTypeFilters()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--participant", ArchiveQueryHarness.AliceId,
            "--type", "text",
            "--since", "2026-01-05",
            "--until", "2026-01-05T23:59:59+08:00",
            "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            [ArchiveQueryHarness.NullOrderKeyId],
            document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task MessageListUsesEqualTimestampOrderingRatherThanInsertionOrder()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--since", "2026-01-05", "--json");

        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            [
                ArchiveQueryHarness.NullOrderKeyId,
                ArchiveQueryHarness.EqualTimestampFirstId,
                ArchiveQueryHarness.EqualTimestampSecondId,
            ],
            document.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));
    }

    // ---- message list: human output ---------------------------------------

    [Fact]
    public async Task MessageListHumanOutputIsConciseAndNotJson()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--limit", "1");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Messages (1)", stdout, StringComparison.Ordinal);
        Assert.Contains(ArchiveQueryHarness.FirstId, stdout, StringComparison.Ordinal);
        Assert.Contains("morning", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("{", stdout, StringComparison.Ordinal);
        Assert.Contains("Querying archived messages", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageListQuietSuppressesProgressButNotTheResult()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--quiet");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);
        Assert.Contains("Messages (", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessageListJsonOutputCarriesNoProgressOrAnsiDecoration()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (_, stdout, stderr) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");

        Assert.DoesNotContain('\u001b', stdout);
        Assert.Empty(stderr);
    }

    // ---- message list: failures -------------------------------------------

    [Fact]
    public async Task UnknownConversationFailsDeterministically()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider,
            "message", "list", "--conversation", "g_0000000000000fff", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("conversation_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("error:", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingConversationOptionIsAUsageFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "message", "list", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownOptionIsAUsageFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--bogus", "1", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MalformedCursorIsACursorValidationFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--cursor", "opaque-garbage", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("cursor_invalid", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("--limit", "0")]
    [InlineData("--limit", "501")]
    [InlineData("--limit", "many")]
    [InlineData("--since", "yesterday")]
    [InlineData("--type", "quote")]
    [InlineData("--participant", "alice")]
    public async Task InvalidFilterValuesAreUsageFailures(string option, string value)
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            option, value, "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnreadableArchiveIsAnArchiveUnavailableFailure()
    {
        using var harness = await SeededAsync();
        await File.WriteAllTextAsync(harness.ArchivePath, "this is not a SQLite database");
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("archive_unavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingOrUnknownSubcommandIsAUsageFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (missing, missingOut, _) = await RunAsync(provider, "message", "--json");
        Assert.Equal(ExitCode.UsageError, missing);
        using (var document = ParseSingleDocument(missingOut))
        {
            Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var (unknown, unknownOut, _) = await RunAsync(provider, "message", "bogus", "--json");
        Assert.Equal(ExitCode.UsageError, unknown);
        using (var document = ParseSingleDocument(unknownOut))
        {
            Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    // ---- context -----------------------------------------------------------

    [Fact]
    public async Task ContextJsonPinsTheBeforeTargetAfterShape()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider, "context", ArchiveQueryHarness.ImageId, "--before", "2", "--after", "2", "--json");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);

        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;

        Assert.Equal(
            ["after", "before", "conversation_id", "message", "message_id"],
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal(ArchiveQueryHarness.ImageId, root.GetProperty("message_id").GetString());
        Assert.Equal(ArchiveQueryHarness.GroupConversationId, root.GetProperty("conversation_id").GetString());
        Assert.Equal(ArchiveQueryHarness.ImageId, root.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal(
            [ArchiveQueryHarness.SecondId, ArchiveQueryHarness.UnknownId],
            root.GetProperty("before").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
        Assert.Equal(
            [ArchiveQueryHarness.PartialId, ArchiveQueryHarness.SystemId],
            root.GetProperty("after").EnumerateArray().Select(item => item.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task ContextDefaultsToTheDocumentedWindowSize()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider, "context", ArchiveQueryHarness.EqualTimestampSecondId, "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(8, document.RootElement.GetProperty("before").GetArrayLength());
        Assert.Equal(0, document.RootElement.GetProperty("after").GetArrayLength());
    }

    [Fact]
    public async Task ContextHumanOutputSeparatesBeforeTargetAndAfter()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider, "context", ArchiveQueryHarness.ImageId, "--before", "1", "--after", "1");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("before (1):", stdout, StringComparison.Ordinal);
        Assert.Contains("message:", stdout, StringComparison.Ordinal);
        Assert.Contains("after (1):", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("{", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownMessageIsADeterministicFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "context", "m_0000000000000fff", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("message_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("two")]
    public async Task InvalidContextBoundsAreUsageFailures(string value)
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(
            provider, "context", ArchiveQueryHarness.ImageId, "--before", value, "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task MissingMessageIdIsAUsageFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "context", "--json");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- automation safety --------------------------------------------------

    [Fact]
    public async Task NoInputNeverPromptsAndStillSucceeds()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(
            provider,
            "message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId,
            "--json", "--no-input");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(harness.GroupMessages.Count, document.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task NoInputTurnsMissingInformationIntoADeterministicFailure()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "context", "--json", "--no-input");

        Assert.Equal(ExitCode.UsageError, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("?", stdout, StringComparison.Ordinal);
        Assert.Contains("error:", stderr, StringComparison.Ordinal);
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
            ["message", "list", "--conversation", ArchiveQueryHarness.GroupConversationId, "--json"],
            stdout,
            stderr,
            provider,
            cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        using var document = ParseSingleDocument(stdout.ToString());
        Assert.Equal("cancelled", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task QueryCommandsAreAdvertisedByHelp()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "--help", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var commands = document.RootElement.GetProperty("commands").EnumerateArray()
            .Select(command => command.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("message", commands);
        Assert.Contains("context", commands);
    }
}