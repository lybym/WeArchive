using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Core.Abstractions;
using WeArchive.Infrastructure;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for <c>wearchive sync --conversation</c> after Issue #49 converged it on the
/// preservation-first capture -&gt; Raw Vault -&gt; conversation ingest path
/// (docs/PRD.md FR-04/FR-14/FR-28/FR-29, docs/CLI.md).
/// <para>
/// The command is exercised through <see cref="CliHost.RunAsync"/> against the shipped composition
/// root and the synthetic WeChat source, so it is measured only as a transport adapter: exactly one
/// JSON document on stdout, human diagnostics on stderr, and the documented exit-code family for a
/// first sync, an unchanged <c>no_change</c> sync, a changed sync, a runtime ingest failure, a
/// capture failure and cancellation.
/// </para>
/// </summary>
public sealed class ConversationSyncCliTests
{
    // ---- first sync ---------------------------------------------------------

    [Fact]
    public async Task FirstSyncReportsSucceededAndPublishesTheConversation()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation
            {
                SourceConversationId = WeChatSyncHarness.DirectId("a"),
                Text = "A",
                MessageCount = 3,
            });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var run = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        var root = document.RootElement;
        Assert.Equal(idA, root.GetProperty("conversation_id").GetString());
        Assert.Equal(harness.AccountId, root.GetProperty("account_id").GetString());
        Assert.Equal(WeChatSyncHarness.ProfileId, root.GetProperty("source_profile_id").GetString());
        Assert.Equal(WeChatSyncHarness.DirectId("a"), root.GetProperty("source_conversation_id").GetString());
        Assert.Equal("succeeded", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("conversations_ingested").GetInt32());
        Assert.Equal("baseline", root.GetProperty("capture_mode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("generation_id").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("previous_generation_id").ValueKind);

        // Progress and diagnostics never pollute the machine document.
        Assert.Empty(run.Stderr);
        Assert.Equal(3, (await harness.MessagesAsync(idA)).Count);
    }

    [Fact]
    public async Task SyncResolvesByStableConversationIdAndByUpstreamSourceId()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.GroupId("100200300"), Text = "G" });

        var id = harness.StableId(WeChatSyncHarness.GroupId("100200300"));

        var bySourceId = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.GroupId("100200300"), "--json"]);
        Assert.Equal(ExitCode.Success, bySourceId.ExitCode);
        using (var document = ParseSingleJson(bySourceId.Stdout))
        {
            Assert.Equal(id, document.RootElement.GetProperty("conversation_id").GetString());
        }

        harness.AdvanceClock();
        var byStableId = await RunAsync(harness, ["sync", "--conversation", id, "--json"]);
        Assert.Equal(ExitCode.Success, byStableId.ExitCode);
        using (var document = ParseSingleJson(byStableId.Stdout))
        {
            Assert.Equal(id, document.RootElement.GetProperty("conversation_id").GetString());
            Assert.Equal("no_change", document.RootElement.GetProperty("status").GetString());
        }
    }

    // ---- unchanged second sync ---------------------------------------------

    [Fact]
    public async Task UnchangedSecondSyncReportsNoChangeAndCreatesNoDuplicateRecords()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A", MessageCount = 2 });

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);
        var committed = await harness.MessagesAsync(idA);

        harness.AdvanceClock();
        var run = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("no_change", root.GetProperty("status").GetString());
        Assert.Equal(0, root.GetProperty("conversations_ingested").GetInt32());
        // The documented proof that the repeat reused verified evidence instead of reacquiring it.
        Assert.Equal("incremental", root.GetProperty("capture_mode").GetString());

        Assert.Equal(committed.Select(m => m.Id), (await harness.MessagesAsync(idA)).Select(m => m.Id));
    }

    // ---- changed second sync -----------------------------------------------

    [Fact]
    public async Task ChangedSecondSyncReportsSucceededAndAddsOnlyTheNewEvidence()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A",
            MessageCount = 1,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);

        conversation.MessageCount = 2;
        harness.AdvanceClock();
        var run = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);

        Assert.Equal(ExitCode.Success, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        var root = document.RootElement;
        Assert.Equal("succeeded", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("conversations_ingested").GetInt32());
        Assert.Equal("incremental", root.GetProperty("capture_mode").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("previous_generation_id").GetString()));

        var messages = await harness.MessagesAsync(idA);
        Assert.Equal(2, messages.Count);
        Assert.Equal("A 1", messages[0].Text);
        Assert.Equal("A 2", messages[1].Text);
    }

    // ---- human output, --quiet, --no-input ---------------------------------

    [Fact]
    public async Task HumanOutputKeepsTheResultOnStdoutAndProgressOnStderr()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        var run = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a")]);

        Assert.Equal(ExitCode.Success, run.ExitCode);
        Assert.Contains("Synced conversation", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("result:     succeeded", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Resolving", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Resolving", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("{", run.Stdout, StringComparison.Ordinal);

        // The unchanged repeat is reported as no_change in human mode too.
        harness.AdvanceClock();
        var repeat = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a")]);
        Assert.Equal(ExitCode.Success, repeat.ExitCode);
        Assert.Contains("result:     no_change", repeat.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuietSuppressesProgressOnSuccessButNeverSuppressesAFailure()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        var success = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--quiet"]);
        Assert.Equal(ExitCode.Success, success.ExitCode);
        Assert.Empty(success.Stderr);
        Assert.Contains("Synced conversation", success.Stdout, StringComparison.Ordinal);

        harness.Source.IsAvailable = false;
        var failure = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--quiet", "--json"]);
        Assert.Equal(ExitCode.Failure, failure.ExitCode);
        using var document = ParseSingleJson(failure.Stdout);
        Assert.Equal(CliErrorCode.CaptureFailed, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failure.Stderr), "--quiet must never suppress a failure");
    }

    [Fact]
    public async Task CaptureDiagnosticsAreReportedOnStderrOnly()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);

        // An adapter version change invalidates the published capture checkpoint, so the next
        // capture widens to a full consistent snapshot and records the documented fallback
        // diagnostic (docs/DEVELOPMENT.md section 10.3.1).
        harness.Capture.CaptureAdapterVersion = harness.Capture.CaptureAdapterVersion + "-next";
        harness.AdvanceClock();

        var human = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a")]);
        Assert.Equal(ExitCode.Success, human.ExitCode);
        Assert.Contains("capture_full_fallback", human.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("capture_full_fallback", human.Stdout, StringComparison.Ordinal);

        // The machine document stays a single JSON document with no diagnostic prose in it.
        harness.AdvanceClock();
        var json = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);
        Assert.Equal(ExitCode.Success, json.ExitCode);
        Assert.Empty(json.Stderr);
        using var document = ParseSingleJson(json.Stdout);
        Assert.Equal("no_change", document.RootElement.GetProperty("status").GetString());
    }

    // ---- failures and cancellation -----------------------------------------

    [Fact]
    public async Task RuntimeIngestFailureKeepsThePublishedGenerationAndExitsOne()
    {
        using var temp = new TempDirectory();
        var conversation = new SyntheticCaptureConversation
        {
            SourceConversationId = WeChatSyncHarness.DirectId("a"),
            Text = "A",
            MessageCount = 2,
        };
        using var harness = WeChatSyncHarness.Create(temp, conversation);

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);
        var committed = await harness.MessagesAsync(idA);

        conversation.MessageShardUnreadable = true;
        harness.AdvanceClock();
        var run = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);

        Assert.Equal(ExitCode.Failure, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(CliErrorCode.Failure, document.RootElement.GetProperty("error").GetProperty("code").GetString());

        // R2: the in-flight conversation rolled back and no partial result document was emitted...
        Assert.False(document.RootElement.TryGetProperty("status", out _));
        Assert.Equal(committed.Select(m => m.Id), (await harness.MessagesAsync(idA)).Select(m => m.Id));

        // ...while the successfully published generation is retained for a later retry.
        Assert.Equal(2, (await harness.GenerationsAsync()).Count);
    }

    [Fact]
    public async Task CaptureFailureIsReportedAsCaptureFailedAndPublishesNothing()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });
        harness.Source.IsAvailable = false;

        var run = await RunAsync(harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"]);

        Assert.Equal(ExitCode.Failure, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(CliErrorCode.CaptureFailed, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(await harness.GenerationsAsync());
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task CancellationDuringIngestExits130AndRollsBackTheInFlightConversation()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        using var harness = WeChatSyncHarness.Create(
            temp,
            [new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" }],
            services => services.AddSingleton<IConversationIngestService>(provider =>
                new CancellingIngest(provider.GetRequiredService<RawVaultIngestService>(), cancellation)));

        var idA = harness.StableId(WeChatSyncHarness.DirectId("a"));
        var run = await RunAsync(
            harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"], cancellation.Token);

        Assert.Equal(ExitCode.Cancelled, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(CliErrorCode.Cancelled, document.RootElement.GetProperty("error").GetProperty("code").GetString());

        // The capture had already published its generation before the ingest was cancelled, and the
        // generation survives; the in-flight conversation and its checkpoint were rolled back.
        Assert.Single(await harness.GenerationsAsync());
        Assert.Null(await harness.FindConversationAsync(WeChatSyncHarness.DirectId("a")));
        Assert.Null(await harness.ConversationCheckpointAsync(idA));
    }

    [Fact]
    public async Task APreCancelledRunExits130AndPublishesNothing()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var run = await RunAsync(
            harness, ["sync", "--conversation", WeChatSyncHarness.DirectId("a"), "--json"], cancellation.Token);

        Assert.Equal(ExitCode.Cancelled, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(CliErrorCode.Cancelled, document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(await harness.GenerationsAsync());
        Assert.Empty(await harness.ConversationsAsync());
    }

    [Fact]
    public async Task AConversationNotPresentInTheCapturedEvidenceExitsOneWithAnOperationFailure()
    {
        using var temp = new TempDirectory();
        using var harness = WeChatSyncHarness.Create(temp,
            new SyntheticCaptureConversation { SourceConversationId = WeChatSyncHarness.DirectId("a"), Text = "A" });

        // The live surface reports one more conversation than the capture preserves, so the selector
        // resolves and the captured evidence then cannot produce canonical data for it. That is an
        // operation failure (the same stable-id selector the archive reports, docs/DATA_MODEL.md
        // section 16), not a usage error and not a silent success.
        var ghost = WeChatSyncHarness.DirectId("ghost");
        harness.Source.Conversations = [.. harness.Source.Conversations,
            new SyntheticCaptureConversation { SourceConversationId = ghost }];

        var run = await RunAsync(harness, ["sync", "--conversation", harness.StableId(ghost), "--json", "--no-input"]);

        Assert.Equal(ExitCode.Failure, run.ExitCode);
        using var document = ParseSingleJson(run.Stdout);
        Assert.Equal(
            CliErrorCode.ConversationNotFound,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());

        // The capture had already published before the selected conversation failed to resolve.
        Assert.Single(await harness.GenerationsAsync());
        Assert.Empty(await harness.ConversationsAsync());
    }

    // ---- helpers ------------------------------------------------------------

    private static async Task<RunResult> RunAsync(
        WeChatSyncHarness harness,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await CliHost.RunAsync(args, stdout, stderr, harness.Provider, cancellationToken);
        return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonDocument ParseSingleJson(string output)
    {
        var trimmed = output.TrimEnd();
        Assert.NotEmpty(trimmed);
        Assert.False(trimmed.Contains('\n'), $"stdout must contain exactly one JSON document, got: {trimmed}");
        Assert.False(trimmed.Contains('\u001b'), "stdout must not carry ANSI escape decoration");
        return JsonDocument.Parse(trimmed);
    }

    private sealed record RunResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// An ingest decorator that cancels the run's token as the ingest phase begins, so the
    /// CLI-level cancellation semantics of the R2 ingest phase are deterministic instead of racy.
    /// </summary>
    private sealed class CancellingIngest(IConversationIngestService inner, CancellationTokenSource cancellation)
        : IConversationIngestService
    {
        public Task<int> IngestConversationAsync(
            string accountId,
            string conversationSelector,
            IProgress<string>? progress,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return inner.IngestConversationAsync(accountId, conversationSelector, progress, cancellationToken);
        }
    }
}
