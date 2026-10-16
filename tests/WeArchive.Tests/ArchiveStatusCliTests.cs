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
/// CLI contract tests for <c>wearchive status</c> (issue #107, docs/CLI.md, docs/HARNESS.md
/// section 10): the pinned JSON document shape, the independent fresh / not-ingested / stale /
/// unknown stage fixtures, source-neutrality (no vault paths, fingerprints, checkpoint payloads or
/// source keys), stdout/stderr separation and the documented exit codes.
/// <para>
/// Freshness semantics themselves are owned by <see cref="ArchiveQueryService"/> and pinned by
/// <see cref="ArchiveQueryServiceTests"/>; these tests pin how the CLI transports them. The provider
/// registers a source adapter that fails on every call, so a passing test also proves that status
/// never contacts live WeChat.
/// </para>
/// </summary>
public sealed class ArchiveStatusCliTests
{
    private static ServiceProvider BuildProvider(ArchiveQueryHarness harness)
    {
        var services = new ServiceCollection();
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

    private static void PublishGeneration(ArchiveQueryHarness harness, string generationId, DateTimeOffset captureTime)
    {
        harness.Vault.AccountIds = [ArchiveQueryHarness.AccountId];
        harness.Vault.Latest[ArchiveQueryHarness.AccountId] = new RawGenerationSummary
        {
            GenerationId = generationId,
            AccountId = ArchiveQueryHarness.AccountId,
            CaptureTime = captureTime,
            Completeness = RawGenerationCompleteness.Complete,
            ArtifactCount = 3,
            EvidenceFingerprint = "fingerprint-that-must-not-be-published",
        };
    }

    private static async Task CommitConversationIngestAsync(
        ArchiveQueryHarness harness,
        string generationId,
        DateTimeOffset updatedAt)
    {
        await harness.Store.SetIngestCheckpointAsync(new IngestCheckpoint
        {
            Id = $"ingest_{generationId}",
            AccountId = ArchiveQueryHarness.AccountId,
            AdapterFamily = "wechat-windows",
            ScopeKind = "conversation",
            ScopeId = ArchiveQueryHarness.GroupConversationId,
            CheckpointJson = $$"""{"version":1,"reader_version":"0.1.0","generation_id":"{{generationId}}","evidence_fingerprint":"checkpoint-fingerprint"}""",
            UpdatedAt = updatedAt,
        }, CancellationToken.None);
    }

    // ---- JSON success: the fresh fixture ------------------------------------

    [Fact]
    public async Task StatusJsonEmitsThePinnedDocumentShape()
    {
        using var harness = await SeededAsync();
        PublishGeneration(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        await CommitConversationIngestAsync(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        await harness.Store.SetIngestCheckpointAsync(new IngestCheckpoint
        {
            Id = "ingest_scan",
            AccountId = ArchiveQueryHarness.AccountId,
            AdapterFamily = "wechat-windows",
            ScopeKind = "account",
            ScopeId = ArchiveQueryHarness.AccountId,
            CheckpointJson = """{"version":1,"reader_version":"0.1.0","covered_generation_ids":["gen_00000000000000aa"]}""",
            UpdatedAt = ArchiveQueryHarness.At(3, 1, 13),
        }, CancellationToken.None);
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "status", "--json");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);

        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal(
            ["basis", "canonical", "capture", "ingest"],
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));

        var basis = root.GetProperty("basis");
        Assert.Equal(
            ["archive_path", "queried_at", "source"],
            basis.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("canonical_archive", basis.GetProperty("source").GetString());
        Assert.Equal(harness.ArchivePath, basis.GetProperty("archive_path").GetString());
        Assert.Equal("2026-03-01T12:00:00+08:00", basis.GetProperty("queried_at").GetString());

        var canonical = root.GetProperty("canonical");
        Assert.Equal(
            ["account_count", "conversation_count", "last_message_at", "message_count", "participant_count"],
            canonical.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(1, canonical.GetProperty("account_count").GetInt32());
        Assert.Equal(2, canonical.GetProperty("conversation_count").GetInt32());
        Assert.Equal(0, canonical.GetProperty("participant_count").GetInt32());
        Assert.Equal(12, canonical.GetProperty("message_count").GetInt32());
        Assert.Equal("2026-02-02T09:00:00+08:00", canonical.GetProperty("last_message_at").GetString());

        var capture = root.GetProperty("capture");
        Assert.Equal(
            ["accounts", "unavailable_reason"],
            capture.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, capture.GetProperty("unavailable_reason").ValueKind);
        var captureAccount = Assert.Single(capture.GetProperty("accounts").EnumerateArray());
        Assert.Equal(
            ["account_id", "artifact_count", "capture_time", "completeness", "generation_id", "previous_generation_id", "unavailable_reason"],
            captureAccount.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(ArchiveQueryHarness.AccountId, captureAccount.GetProperty("account_id").GetString());
        Assert.Equal("gen_00000000000000aa", captureAccount.GetProperty("generation_id").GetString());
        Assert.Equal("2026-03-01T12:00:00+08:00", captureAccount.GetProperty("capture_time").GetString());
        Assert.Equal("complete", captureAccount.GetProperty("completeness").GetString());
        Assert.Equal(3, captureAccount.GetProperty("artifact_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, captureAccount.GetProperty("previous_generation_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, captureAccount.GetProperty("unavailable_reason").ValueKind);

        var ingest = root.GetProperty("ingest");
        Assert.Equal(["accounts"], ingest.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        var ingestAccount = Assert.Single(ingest.GetProperty("accounts").EnumerateArray());
        Assert.Equal(
            ["account_id", "last_account_scan_at", "last_ingest_at", "latest_ingested_conversation_id", "latest_ingested_generation_id"],
            ingestAccount.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(ArchiveQueryHarness.AccountId, ingestAccount.GetProperty("account_id").GetString());
        Assert.Equal("2026-03-01T12:00:00+08:00", ingestAccount.GetProperty("last_ingest_at").GetString());
        Assert.Equal("gen_00000000000000aa", ingestAccount.GetProperty("latest_ingested_generation_id").GetString());
        Assert.Equal(ArchiveQueryHarness.GroupConversationId, ingestAccount.GetProperty("latest_ingested_conversation_id").GetString());
        Assert.Equal("2026-03-01T13:00:00+08:00", ingestAccount.GetProperty("last_account_scan_at").GetString());
    }

    // ---- independent stage fixtures -----------------------------------------

    [Fact]
    public async Task CapturedButNotIngestedStaysVisibleInsteadOfReadingAsIngested()
    {
        using var harness = await SeededAsync();
        PublishGeneration(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        Assert.Equal(
            "gen_00000000000000aa",
            Assert.Single(root.GetProperty("capture").GetProperty("accounts").EnumerateArray())
                .GetProperty("generation_id").GetString());

        var ingest = Assert.Single(root.GetProperty("ingest").GetProperty("accounts").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, ingest.GetProperty("last_ingest_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, ingest.GetProperty("latest_ingested_generation_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, ingest.GetProperty("latest_ingested_conversation_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, ingest.GetProperty("last_account_scan_at").ValueKind);
    }

    [Fact]
    public async Task StaleIngestBehindANewerCaptureIsVisibleWithoutBeingInferredAway()
    {
        using var harness = await SeededAsync();
        await CommitConversationIngestAsync(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        PublishGeneration(harness, "gen_00000000000000bb", ArchiveQueryHarness.At(3, 1, 20));
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        // The two stages stay independent facts: the newer captured generation and the older
        // ingested one are both reported, neither is hidden behind the other.
        Assert.Equal(
            "gen_00000000000000bb",
            Assert.Single(root.GetProperty("capture").GetProperty("accounts").EnumerateArray())
                .GetProperty("generation_id").GetString());
        var ingest = Assert.Single(root.GetProperty("ingest").GetProperty("accounts").EnumerateArray());
        Assert.Equal("gen_00000000000000aa", ingest.GetProperty("latest_ingested_generation_id").GetString());
        Assert.Equal("2026-03-01T12:00:00+08:00", ingest.GetProperty("last_ingest_at").GetString());
    }

    [Fact]
    public async Task AnUnreadablePreservationStoreDegradesCaptureInsteadOfTheReport()
    {
        using var harness = await SeededAsync();
        harness.Vault.Failure = new IOException("vault root disappeared");
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

        // Reporting unavailability is the purpose of the command: the canonical half survives and
        // the capture stage says why it cannot be read.
        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var root = document.RootElement;
        var capture = root.GetProperty("capture");
        Assert.Contains("IOException", capture.GetProperty("unavailable_reason").GetString(), StringComparison.Ordinal);
        var captureAccount = Assert.Single(capture.GetProperty("accounts").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, captureAccount.GetProperty("generation_id").ValueKind);

        Assert.Equal(12, root.GetProperty("canonical").GetProperty("message_count").GetInt32());
        var ingest = Assert.Single(root.GetProperty("ingest").GetProperty("accounts").EnumerateArray());
        Assert.Equal(ArchiveQueryHarness.AccountId, ingest.GetProperty("account_id").GetString());
    }

    [Fact]
    public async Task APerAccountGenerationFailureIsReportedOnThatAccount()
    {
        using var harness = await SeededAsync();
        harness.Vault.AccountIds = [ArchiveQueryHarness.AccountId];
        harness.Vault.AccountFailures[ArchiveQueryHarness.AccountId] =
            new ArgumentException("duplicate generation identity");
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

        Assert.Equal(ExitCode.Success, exit);
        using var document = ParseSingleDocument(stdout);
        var captureAccount = Assert.Single(
            document.RootElement.GetProperty("capture").GetProperty("accounts").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, captureAccount.GetProperty("generation_id").ValueKind);
        Assert.Contains(
            "ArgumentException",
            captureAccount.GetProperty("unavailable_reason").GetString(),
            StringComparison.Ordinal);
    }

    // ---- source neutrality and private-data isolation ------------------------

    [Fact]
    public async Task StatusNeverLeaksPreservationInternalsOrCheckpointPayloads()
    {
        using var harness = await SeededAsync();
        PublishGeneration(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        await CommitConversationIngestAsync(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

        Assert.Equal(ExitCode.Success, exit);
        // No vault root, no evidence fingerprint (fixture or checkpoint payload), no checkpoint
        // encoding: the status document names stages and stable ids only.
        Assert.DoesNotContain("fingerprint", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<fake-vault>", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reader_version", stdout, StringComparison.Ordinal);
        Assert.Contains("gen_00000000000000aa", stdout, StringComparison.Ordinal);
    }

    // ---- boundary fixtures and failure mapping -------------------------------

    [Fact]
    public async Task StatusOnAnEmptyArchiveIsASuccessfulZeroReport()
    {
        var harness = new ArchiveQueryHarness();
        try
        {
            using var provider = BuildProvider(harness);

            var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

            Assert.Equal(ExitCode.Success, exit);
            using var document = ParseSingleDocument(stdout);
            var root = document.RootElement;
            var canonical = root.GetProperty("canonical");
            Assert.Equal(0, canonical.GetProperty("account_count").GetInt32());
            Assert.Equal(0, canonical.GetProperty("message_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, canonical.GetProperty("last_message_at").ValueKind);
            Assert.Empty(root.GetProperty("capture").GetProperty("accounts").EnumerateArray());
            Assert.Empty(root.GetProperty("ingest").GetProperty("accounts").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("capture").GetProperty("unavailable_reason").ValueKind);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task UnreadableArchiveIsArchiveUnavailable()
    {
        using var harness = await SeededAsync();
        await File.WriteAllTextAsync(harness.ArchivePath, "this is not a SQLite database");
        using var provider = BuildProvider(harness);

        var (exit, stdout, _) = await RunAsync(provider, "status", "--json");

        Assert.Equal(ExitCode.Failure, exit);
        using var document = ParseSingleDocument(stdout);
        Assert.Equal(
            "archive_unavailable",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownOptionOrPositionalArgumentIsAUsageError()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (optionExit, optionOut, _) = await RunAsync(provider, "status", "--conversation", "g_x", "--json");
        Assert.Equal(ExitCode.UsageError, optionExit);
        using (var document = ParseSingleDocument(optionOut))
        {
            Assert.Equal(
                "usage_error",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        var (positionalExit, positionalOut, _) = await RunAsync(provider, "status", "extra", "--json");
        Assert.Equal(ExitCode.UsageError, positionalExit);
        using (var document = ParseSingleDocument(positionalOut))
        {
            Assert.Equal(
                "usage_error",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }

    // ---- human output, process contract, discovery ---------------------------

    [Fact]
    public async Task StatusHumanOutputSummarizesEveryStageAndKeepsJsonOffStdout()
    {
        using var harness = await SeededAsync();
        PublishGeneration(harness, "gen_00000000000000aa", ArchiveQueryHarness.At(3, 1, 12));
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "status");

        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.ToString();
        Assert.StartsWith("Archive status (as of", output, StringComparison.Ordinal);
        Assert.Contains("canonical: 1 account(s), 2 conversation(s)", output);
        Assert.Contains("12 message(s)", output);
        Assert.Contains("capture: 1 account(s)", output);
        Assert.Contains($"{ArchiveQueryHarness.AccountId}  gen_00000000000000aa", output);
        Assert.Contains("complete", output);
        Assert.Contains("ingest: 1 account(s)", output);
        Assert.Contains("not ingested yet", output);
        Assert.DoesNotContain("{", output);
        // Progress belongs on stderr in human mode.
        Assert.Contains("Reading archive freshness", stderr.ToString());
    }

    [Fact]
    public async Task StatusJsonSuppressesProgressAndAcceptsNoInputAndQuiet()
    {
        using var harness = await SeededAsync();
        using var provider = BuildProvider(harness);

        var (exit, stdout, stderr) = await RunAsync(provider, "status", "--json", "--no-input", "--quiet");

        Assert.Equal(ExitCode.Success, exit);
        Assert.Empty(stderr);
        Assert.NotEmpty(stdout.ToString());
    }

    [Fact]
    public async Task HelpListsTheStatusCommand()
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
        Assert.Contains("status", commands);
    }
}
