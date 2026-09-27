using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// CLI contract tests for <c>wearchive capture</c>: command parsing, JSON shape,
/// stdout/stderr separation, exit codes and the <c>--no-input</c> contract.
/// Issue #22 acceptance criteria: "`wearchive capture --json --no-input` obeys the existing
/// machine-process contract" and "Human/progress diagnostics go to stderr and never
/// contaminate JSON stdout."
/// </summary>
public sealed class CaptureCliTests
{
    private static ServiceProvider BuildProvider(TempDirectory temp) =>
        BuildProvider(temp, new FixtureCaptureAdapter(), new FixedClock());

    /// <summary>
    /// Same contract, but with a test-owned capture adapter and clock so incremental capture
    /// (Issue #25) can be driven through the CLI across two runs.
    /// </summary>
    private static ServiceProvider BuildProvider(
        TempDirectory temp,
        ISourceCaptureAdapter captureAdapter,
        IClock clock)
    {
        var fixture = new FixtureSourceAdapter();
        var archivePath = temp.Combine("archive.db");
        var vaultRoot = temp.Combine("vault");

        var services = new ServiceCollection();
        services.AddWeArchiveCore(archivePath, vaultRoot);
        services.AddSingleton(clock);
        services.AddSingleton<ISourceAdapter>(fixture);
        services.AddSingleton(captureAdapter);
        return services.BuildServiceProvider();
    }

    private static async Task<(int exit, string stdout, string stderr)> RunAsync(
        ServiceProvider provider, string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliHost.RunAsync(args, stdout, stderr, provider, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task CaptureJsonEmitsExactlyOneDocumentOnStdout()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, stderr) = await RunAsync(
            provider, ["capture", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.TrimEnd();
        using var doc = JsonDocument.Parse(output);

        Assert.Equal("baseline", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("complete", doc.RootElement.GetProperty("completeness").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("artifact_count").GetInt32());
        Assert.StartsWith("gen_", doc.RootElement.GetProperty("generation_id").GetString());
        Assert.True(doc.RootElement.GetProperty("coverage_summary").TryGetProperty("expected", out _));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("coverage").ValueKind);

        // stdout must be exactly one JSON document with no trailing content.
        Assert.False(output.Contains('\n'));
        // stderr must be empty in --json mode (progress suppressed).
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task CaptureJsonReportsPartialCoverageSummaryWithoutSourceEvidence()
    {
        using var temp = new TempDirectory();
        var adapter = new SyntheticCaptureAdapter();
        adapter.Partitions["session"] = "s1";
        adapter.Partitions["voice"] = "v1";
        adapter.Partitions["media"] = "m1";
        adapter.Unreadable.Add("voice");
        adapter.Unsupported.Add("media");
        using var provider = BuildProvider(temp, adapter, new FixedClock());

        var (exit, stdout, _) = await RunAsync(provider, ["capture", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.TrimEnd());
        Assert.Equal("baseline", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("partial", doc.RootElement.GetProperty("completeness").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("coverage").GetArrayLength());

        var summary = doc.RootElement.GetProperty("coverage_summary");
        Assert.Equal(3, summary.GetProperty("expected").GetInt32());
        Assert.Equal(1, summary.GetProperty("captured").GetInt32());
        Assert.Equal(0, summary.GetProperty("reused").GetInt32());
        Assert.Equal(1, summary.GetProperty("unavailable").GetInt32());
        Assert.Equal(1, summary.GetProperty("unsupported").GetInt32());

        // Every coverage gap is attributable to its partition from the JSON alone, and the reason
        // never carries the fingerprint or checksum that proves reuse safety.
        foreach (var entry in doc.RootElement.GetProperty("coverage").EnumerateArray())
        {
            var status = entry.GetProperty("status").GetString();
            var hasDiagnostic = entry.TryGetProperty("diagnostic", out var diagnostic)
                && !string.IsNullOrWhiteSpace(diagnostic.GetString());
            Assert.Equal(status != "captured", hasDiagnostic);
        }

        // The JSON contract never exposes the source fingerprints used to prove reuse safety.
        Assert.DoesNotContain("s1", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("v1", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("m1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureJsonReportsIncrementalReuseAfterCompleteBaseline()
    {
        using var temp = new TempDirectory();
        var adapter = new SyntheticCaptureAdapter();
        adapter.Partitions["session"] = "s1";
        adapter.Partitions["contact"] = "c1";
        var clock = new FixedClock();
        using var provider = BuildProvider(temp, adapter, clock);

        var (firstExit, _, _) = await RunAsync(provider, ["capture", "--json", "--no-input"]);
        Assert.Equal(ExitCode.Success, firstExit);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        adapter.Partitions["contact"] = "c2";
        adapter.Partitions["message_0"] = "m1";
        var (exit, stdout, _) = await RunAsync(provider, ["capture", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.TrimEnd());
        Assert.Equal("incremental", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("complete", doc.RootElement.GetProperty("completeness").GetString());

        var summary = doc.RootElement.GetProperty("coverage_summary");
        Assert.Equal(3, summary.GetProperty("expected").GetInt32());
        Assert.Equal(2, summary.GetProperty("captured").GetInt32());
        Assert.Equal(1, summary.GetProperty("reused").GetInt32());
        Assert.Equal(0, summary.GetProperty("unavailable").GetInt32());
        Assert.Equal(0, summary.GetProperty("unsupported").GetInt32());
    }

    [Fact]
    public async Task CaptureJsonReportsUnsupportedCoverageOnACompleteRun()
    {
        using var temp = new TempDirectory();
        var adapter = new SyntheticCaptureAdapter();
        adapter.Partitions["session/session.db"] = "s1";
        adapter.Partitions["contact/contact.db"] = "c1";
        adapter.Partitions["migrate/unspportmsg.db"] = "u1";
        adapter.Unsupported.Add("migrate/unspportmsg.db");
        using var provider = BuildProvider(temp, adapter, new FixedClock());

        var (exit, stdout, _) = await RunAsync(provider, ["capture", "--json", "--no-input"]);

        // A generated-but-unsupported partition is visible in the rollup without downgrading the
        // verdict, which is the Issue #37 contract the CLI must expose honestly.
        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.TrimEnd());
        Assert.Equal("complete", doc.RootElement.GetProperty("completeness").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("coverage").GetArrayLength());

        var summary = doc.RootElement.GetProperty("coverage_summary");
        Assert.Equal(3, summary.GetProperty("expected").GetInt32());
        Assert.Equal(2, summary.GetProperty("captured").GetInt32());
        Assert.Equal(0, summary.GetProperty("reused").GetInt32());
        Assert.Equal(0, summary.GetProperty("unavailable").GetInt32());
        Assert.Equal(1, summary.GetProperty("unsupported").GetInt32());

        var unsupported = doc.RootElement.GetProperty("coverage").EnumerateArray()
            .Single(e => e.GetProperty("partition_id").GetString() == "migrate/unspportmsg.db");
        Assert.Equal("unsupported", unsupported.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(unsupported.GetProperty("diagnostic").GetString()));
    }

    [Fact]
    public async Task CaptureJsonReportsUnclassifiedCoverageAsPartialAndUnsupported()
    {
        using var temp = new TempDirectory();
        var adapter = new SyntheticCaptureAdapter();
        adapter.Partitions["session/session.db"] = "s1";
        adapter.Partitions["newpart/new.db"] = "n1";
        adapter.Unclassified.Add("newpart/new.db");
        using var provider = BuildProvider(temp, adapter, new FixedClock());

        var (exit, stdout, _) = await RunAsync(provider, ["capture", "--json", "--no-input"]);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.TrimEnd());
        Assert.Equal("partial", doc.RootElement.GetProperty("completeness").GetString());

        var summary = doc.RootElement.GetProperty("coverage_summary");
        Assert.Equal(2, summary.GetProperty("expected").GetInt32());
        Assert.Equal(1, summary.GetProperty("captured").GetInt32());
        Assert.Equal(0, summary.GetProperty("unavailable").GetInt32());
        Assert.Equal(1, summary.GetProperty("unsupported").GetInt32());

        var codes = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => d.GetProperty("code").GetString())
            .ToArray();
        Assert.Contains(DiagnosticCodes.PartitionUnclassified, codes);
    }

    [Fact]
    public async Task CaptureHumanModeWritesProgressToStderr()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, stderr) = await RunAsync(provider, ["capture"]);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Captured", stdout);
        // Human progress goes to stderr, not stdout.
        Assert.NotEmpty(stderr);
        Assert.DoesNotContain("Describing", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureQuietSuppressesProgressButNotResult()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, stderr) = await RunAsync(provider, ["capture", "--quiet"]);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Captured", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task CaptureNoInputNeverPrompts()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, _) = await RunAsync(provider, ["capture", "--no-input"]);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("gen_", stdout);
    }

    [Fact]
    public async Task CaptureWithUnknownOptionReturnsUsageError()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, _) = await RunAsync(provider, ["capture", "--bogus", "--json"]);

        Assert.Equal(ExitCode.UsageError, exit);
        var doc = JsonDocument.Parse(stdout.TrimEnd());
        Assert.Equal("usage_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task CaptureAppearsInHelp()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, _) = await RunAsync(provider, ["--help", "--json"]);

        Assert.Equal(ExitCode.Success, exit);
        var doc = JsonDocument.Parse(stdout.TrimEnd());
        var commands = doc.RootElement.GetProperty("commands");
        var names = new List<string>();
        foreach (var cmd in commands.EnumerateArray())
        {
            names.Add(cmd.GetProperty("name").GetString()!);
        }
        Assert.Contains("capture", names);
    }

    [Fact]
    public async Task CaptureAccountNotFoundReturnsExitOne()
    {
        using var temp = new TempDirectory();
        using var provider = BuildProvider(temp);

        var (exit, stdout, _) = await RunAsync(
            provider, ["capture", "--account", "nonexistent", "--json"]);

        Assert.Equal(ExitCode.Failure, exit);
        var doc = JsonDocument.Parse(stdout.TrimEnd());
        Assert.Equal("account_not_found", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
