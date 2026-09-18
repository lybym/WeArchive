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
    private static ServiceProvider BuildProvider(TempDirectory temp)
    {
        var fixture = new FixtureSourceAdapter();
        var clock = new FixedClock();
        var archivePath = temp.Combine("archive.db");
        var vaultRoot = temp.Combine("vault");

        var services = new ServiceCollection();
        services.AddWeArchiveCore(archivePath, vaultRoot);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISourceAdapter>(fixture);
        services.AddSingleton<ISourceCaptureAdapter, FixtureCaptureAdapter>();
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

        // stdout must be exactly one JSON document with no trailing content.
        Assert.False(output.Contains('\n'));
        // stderr must be empty in --json mode (progress suppressed).
        Assert.Empty(stderr);
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
