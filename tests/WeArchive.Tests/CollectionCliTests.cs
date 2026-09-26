using System.Text.Json;
using WeArchive.Cli.CommandLine;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for the Collection scope (docs/PRD.md FR-23/FR-28/FR-29, docs/CLI.md).
/// <para>
/// The commands are exercised through <see cref="CliHost.RunAsync"/> against a temporary archive,
/// the authoritative configuration file and the synthetic capture source, so the CLI is measured
/// only as a transport adapter: exactly one JSON document on stdout, human diagnostics on stderr,
/// and the documented exit-code family.
/// </para>
/// </summary>
public sealed class CollectionCliTests
{
    // ---- collection list ---------------------------------------------------

    [Fact]
    public async Task CollectionListReportsNamesInJsonAndHumanModes()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA);

        var json = await RunAsync(harness, ["collection", "list", "--json", "--no-input"]);
        Assert.Equal(ExitCode.Success, json.ExitCode);
        Assert.Empty(json.Stderr);
        using (var document = ParseSingleJson(json.Stdout))
        {
            var items = document.RootElement.EnumerateArray().ToList();
            var item = Assert.Single(items);
            Assert.Equal("project-x", item.GetProperty("name").GetString());
            Assert.Equal(1, item.GetProperty("conversation_count").GetInt32());
            Assert.Equal(0, item.GetProperty("invalid_member_count").GetInt32());
            Assert.Equal(0, item.GetProperty("duplicate_member_count").GetInt32());
        }

        var human = await RunAsync(harness, ["collection", "list", "--no-input"]);
        Assert.Equal(ExitCode.Success, human.ExitCode);
        Assert.Contains("project-x", human.Stdout, StringComparison.Ordinal);
        Assert.Contains("Collections (1)", human.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionListWithoutAConfigurationIsAnEmptyResult()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(harness, ["collection", "list", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Empty(document.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task CollectionListCountsInvalidAndDuplicateDeclarations()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA, idA, "wxid_not_stable");

        var result = await RunAsync(harness, ["collection", "list", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        var item = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(1, item.GetProperty("conversation_count").GetInt32());
        Assert.Equal(1, item.GetProperty("invalid_member_count").GetInt32());
        Assert.Equal(1, item.GetProperty("duplicate_member_count").GetInt32());
    }

    // ---- collection show ---------------------------------------------------

    [Fact]
    public async Task CollectionShowReturnsStableMembershipAndReportsInvalidAndDuplicateEntries()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA, "wxid_not_stable", idA);

        var result = await RunAsync(harness, ["collection", "show", "project-x", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Empty(result.Stderr);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("project-x", document.RootElement.GetProperty("name").GetString());
        Assert.Equal([idA], document.RootElement.GetProperty("conversation_ids")
            .EnumerateArray().Select(v => v.GetString()).ToList());
        Assert.Equal(["wxid_not_stable"], document.RootElement.GetProperty("invalid_conversation_ids")
            .EnumerateArray().Select(v => v.GetString()).ToList());
        Assert.Equal([idA], document.RootElement.GetProperty("duplicate_conversation_ids")
            .EnumerateArray().Select(v => v.GetString()).ToList());
    }

    [Fact]
    public async Task CollectionShowHumanOutputListsMembership()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA);

        var result = await RunAsync(harness, ["collection", "show", "project-x", "--no-input"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains($"Collection: project-x", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(idA, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionShowOfAnUnknownNameIsADeterministicOperationFailure()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        harness.WriteCollection("project-x", harness.StableId(CollectionHarness.DirectId("a")));

        var result = await RunAsync(harness, ["collection", "show", "missing", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Failure, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("collection_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("missing", document.RootElement.GetProperty("error").GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionShowWithoutANameIsAUsageError()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(harness, ["collection", "show", "--json", "--no-input"]);

        Assert.Equal(ExitCode.UsageError, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownCollectionSubcommandIsAUsageError()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(harness, ["collection", "add", "project-x", "--json", "--no-input"]);

        Assert.Equal(ExitCode.UsageError, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- invalid configuration ---------------------------------------------

    [Fact]
    public async Task MalformedConfigurationIsAConfigurationValidationFailure()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        // An unterminated flow mapping is not valid YAML.
        harness.WriteConfiguration("collections: {\n");

        var result = await RunAsync(harness, ["collection", "list", "--json", "--no-input"]);

        // Exit 2 is the documented "usage/configuration validation failure" family. The file is
        // diagnosed, never silently rewritten or silently treated as an empty catalog.
        Assert.Equal(ExitCode.UsageError, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("collection_config_invalid", error.GetProperty("code").GetString());
        Assert.Contains("collections.yaml", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Equal("collections: {\n", File.ReadAllText(harness.ConfigurationPath));
    }

    [Fact]
    public async Task UnsupportedConfigurationSchemaVersionIsAConfigurationValidationFailure()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);
        harness.WriteConfiguration("schema_version: 9.9\ncollections: {}\n");

        var result = await RunAsync(harness, ["collection", "show", "project-x", "--json", "--no-input"]);

        Assert.Equal(ExitCode.UsageError, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("collection_config_invalid", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- sync --collection -------------------------------------------------

    [Fact]
    public async Task SyncCollectionReportsEveryMemberAndSucceeds()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = CollectionHarness.DirectId("a"), Text = "A message" },
            new SyntheticCaptureConversation { SourceConversationId = CollectionHarness.GroupId("100200300"), Text = "C message" });
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        var idC = harness.StableId(CollectionHarness.GroupId("100200300"));
        harness.WriteCollection("project-x", idA, idC);

        var result = await RunAsync(harness, ["sync", "--collection", "project-x", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Empty(result.Stderr);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("project-x", document.RootElement.GetProperty("collection").GetString());
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(harness.AccountId, document.RootElement.GetProperty("account_id").GetString());
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("generation_id").GetString()));
        Assert.Equal("baseline", document.RootElement.GetProperty("capture_mode").GetString());

        var conversations = document.RootElement.GetProperty("conversations").EnumerateArray().ToList();
        Assert.Equal(2, conversations.Count);
        Assert.All(conversations, item => Assert.Equal("succeeded", item.GetProperty("status").GetString()));

        var summary = document.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("requested").GetInt32());
        Assert.Equal(2, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(0, summary.GetProperty("failed").GetInt32());
    }

    /// <summary>
    /// The defining acceptance criterion for a multi-scope run: the process exit status is non-zero
    /// when any requested conversation fails, while the single JSON document still reports the
    /// conversations that succeeded (docs/PRD.md FR-23).
    /// </summary>
    [Fact]
    public async Task SyncCollectionPartialFailureExitsNonZeroAndStillReportsSuccessfulMembers()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = CollectionHarness.DirectId("a"), Text = "A committed" },
            new SyntheticCaptureConversation
            {
                SourceConversationId = CollectionHarness.GroupId("100200300"),
                Text = "C unreadable",
                MessageTablePresent = false,
            });
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        var idC = harness.StableId(CollectionHarness.GroupId("100200300"));
        harness.WriteCollection("project-x", idA, idC);

        var result = await RunAsync(harness, ["sync", "--collection", "project-x", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Failure, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.False(document.RootElement.GetProperty("succeeded").GetBoolean());

        var conversations = document.RootElement.GetProperty("conversations").EnumerateArray().ToList();
        var success = Assert.Single(conversations, item => item.GetProperty("conversation_id").GetString() == idA);
        Assert.Equal("succeeded", success.GetProperty("status").GetString());
        var failure = Assert.Single(conversations, item => item.GetProperty("conversation_id").GetString() == idC);
        Assert.Equal("failed", failure.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("error").GetString()));

        var summary = document.RootElement.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("requested").GetInt32());
        Assert.Equal(1, summary.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, summary.GetProperty("failed").GetInt32());

        // The successful member really committed; the failed one left no state behind.
        Assert.NotNull(await harness.ConversationCheckpointAsync(idA));
        Assert.Null(await harness.FindConversationAsync(CollectionHarness.GroupId("100200300")));
    }

    [Fact]
    public async Task SyncCollectionHumanOutputReportsMembersAndASummary()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = CollectionHarness.DirectId("a"), Text = "A message" });
        var idA = harness.StableId(CollectionHarness.DirectId("a"));
        harness.WriteCollection("project-x", idA);

        var result = await RunAsync(harness, ["sync", "--collection", "project-x", "--no-input"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        Assert.Contains("Synced collection project-x", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(idA, result.Stdout, StringComparison.Ordinal);
        Assert.Contains("summary: 1 succeeded", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncCollectionOfAnUnknownNameFailsWithADeterministicCode()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(harness, ["sync", "--collection", "missing", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Failure, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("collection_not_found", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncCollectionCaptureFailureIsReportedAsAnOperationFailure()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = CollectionHarness.DirectId("a"), Text = "A message" });
        harness.WriteCollection("project-x", harness.StableId(CollectionHarness.DirectId("a")));
        harness.Source.IsAvailable = false;

        var result = await RunAsync(harness, ["sync", "--collection", "project-x", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Failure, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("capture_failed", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncRejectsBothSelectors()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(
            harness, ["sync", "--collection", "project-x", "--conversation", "wxid_a", "--json", "--no-input"]);

        Assert.Equal(ExitCode.UsageError, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task SyncWithoutASelectorIsAUsageError()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(harness, ["sync", "--json", "--no-input"]);

        Assert.Equal(ExitCode.UsageError, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        Assert.Equal("usage_error", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- help surface ------------------------------------------------------

    [Fact]
    public async Task HelpListsTheCollectionCommand()
    {
        using var temp = new TempDirectory();
        using var harness = CollectionHarness.Create(temp);

        var result = await RunAsync(harness, ["--help", "--json"]);

        Assert.Equal(ExitCode.Success, result.ExitCode);
        using var document = ParseSingleJson(result.Stdout);
        var commands = document.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Select(command => command.GetProperty("name").GetString())
            .ToList();
        Assert.Contains("collection", commands);
        Assert.Contains("sync", commands);
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<CliRun> RunAsync(CollectionHarness harness, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CliHost.RunAsync(args, stdout, stderr, harness.Provider, CancellationToken.None);
        return new CliRun(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument ParseSingleJson(string output)
    {
        var trimmed = output.TrimEnd();
        Assert.NotEmpty(trimmed);
        Assert.False(trimmed.Contains('\n'), $"stdout must contain exactly one JSON document, got: {trimmed}");
        Assert.False(trimmed.Contains('\u001b'), "stdout must not carry ANSI escape decoration");
        return JsonDocument.Parse(trimmed);
    }

    private sealed record CliRun(int ExitCode, string Stdout, string Stderr);
}