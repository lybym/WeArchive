using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Cli.Output.Dto;

namespace WeArchive.Tests;

/// <summary>
/// Contract tests for the CLI foundation: command parsing, JSON shape, stdout/stderr
/// separation, --quiet, --no-input and exit-code mapping.
/// docs/PRD.md FR-22, docs/DEVELOPMENT.md section 7 "CLI contract tests".
/// </summary>
public sealed class CliContractTests
{
    /// <summary>
    /// A released version: three numeric parts plus an optional SemVer prerelease suffix, with
    /// no build metadata (docs/CLI.md documents `--version` as the product version).
    /// </summary>
    private const string VersionPattern = @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$";

    // ---- Command-line parser ----

    [Fact]
    public void ParserExtractsJsonFlag()
    {
        var (options, remaining) = CommandLineParser.Parse(["doctor", "--json"]);
        Assert.True(options.Json);
        Assert.Equal(["doctor"], remaining);
    }

    [Fact]
    public void ParserExtractsQuietFlag()
    {
        var (options, _) = CommandLineParser.Parse(["doctor", "--quiet"]);
        Assert.True(options.Quiet);
    }

    [Fact]
    public void ParserExtractsNoInputFlag()
    {
        var (options, _) = CommandLineParser.Parse(["doctor", "--no-input"]);
        Assert.True(options.NoInput);
    }

    [Fact]
    public void ParserExtractsVersionFlag()
    {
        var (options, remaining) = CommandLineParser.Parse(["--version"]);
        Assert.True(options.ShowVersion);
        Assert.Empty(remaining);
    }

    [Fact]
    public void ParserExtractsHelpFlag()
    {
        var (options, _) = CommandLineParser.Parse(["--help"]);
        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void ParserExtractsShortHelpFlag()
    {
        var (options, _) = CommandLineParser.Parse(["-h"]);
        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void ParserLeavesCommandSpecificArgsIntact()
    {
        var (options, remaining) = CommandLineParser.Parse([
            "export", "--conversation", "g_01fd893a7b21c054", "--json", "--no-input",
        ]);
        Assert.True(options.Json);
        Assert.True(options.NoInput);
        Assert.Equal(["export", "--conversation", "g_01fd893a7b21c054"], remaining);
    }

    [Fact]
    public void ParserVersionOverridesHelp()
    {
        var (options, _) = CommandLineParser.Parse(["--version", "--help"]);
        Assert.True(options.ShowVersion);
        Assert.False(options.ShowHelp);
    }

    // ---- Version command / JSON shape ----

    [Fact]
    public async Task VersionJsonEmitsExactlyOneDocument()
    {
        var command = new VersionCommand();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);

        // stdout must be a single valid JSON document with no extra content.
        var output = stdout.ToString().TrimEnd();
        using var doc = JsonDocument.Parse(output);
        // The version is whatever this build was stamped with, so it is compared against the
        // running assembly rather than a literal. Pinning a literal here made the release
        // workflow fail for every version except the default (Issue #9 review, P1 #1).
        Assert.Equal(ProductVersion.Current, doc.RootElement.GetProperty("version").GetString());
        Assert.Matches(VersionPattern, doc.RootElement.GetProperty("version").GetString());
        Assert.Equal("net10.0-windows", doc.RootElement.GetProperty("framework").GetString());
        Assert.Equal("win-x64", doc.RootElement.GetProperty("platform").GetString());

        // stderr must be empty — version is a pure result command.
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task VersionHumanOutputIsNotJson()
    {
        var command = new VersionCommand();
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, GlobalOptions.Default);

        await command.ExecuteAsync(context, [], CancellationToken.None);

        var output = stdout.ToString();
        Assert.Contains("WeArchive", output);
        Assert.DoesNotContain("{", output);
    }

    [Fact]
    public async Task VersionJsonHasNoAnsiDecoration()
    {
        var command = new VersionCommand();
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        await command.ExecuteAsync(context, [], CancellationToken.None);

        var output = stdout.ToString();

        // JSON must start with the opening brace — no leading ANSI or BOM.
        Assert.Equal('{', output.TrimStart()[0]);

        // No ANSI escape. The check is ordinal: a culture-sensitive substring search
        // treats ESC as an ignorable character and would match unrelated text.
        Assert.False(output.Contains('\u001b'), "JSON stdout must not carry ANSI escapes");
    }

    // ---- --version / --help top-level flags via CliHost ----

    [Fact]
    public async Task HostVersionFlagEmitsJson()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["--version", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.ToString().TrimEnd();
        using var doc = JsonDocument.Parse(output);
        // Must agree with the `version` command surface and with the running build's stamp.
        Assert.Equal(ProductVersion.Current, doc.RootElement.GetProperty("version").GetString());
        Assert.Matches(VersionPattern, doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public async Task HostNoArgsShowsHelpAndExitsZero()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync([], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Contains("Usage:", stdout.ToString());
    }

    // ---- --json contract on help / usage-error / failure paths ----
    // docs/PRD.md FR-22, docs/ARCHITECTURE.md section 3.1.1: with --json, stdout must
    // contain exactly one JSON document on every path — never prose, never emptiness.

    [Fact]
    public async Task HostHelpJsonEmitsExactlyOneDocument()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["--help", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = ParseStdoutAsSingleJsonDocument(stdout);
        Assert.Equal("wearchive <command> [options]", doc.RootElement.GetProperty("usage").GetString());

        var commands = doc.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!)
            .ToList();
        Assert.Contains("version", commands);
        Assert.Contains("doctor", commands);
        Assert.Contains("rebuild", commands);

        var options = doc.RootElement.GetProperty("options")
            .EnumerateArray()
            .Select(o => o.GetProperty("name").GetString()!)
            .ToList();
        Assert.Contains("--json", options);

        // Human prose stays out of the machine stream.
        Assert.DoesNotContain("Usage:", stdout.ToString());

        // Help is a successful result, so nothing is reported as a diagnostic.
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task HostNoCommandJsonEmitsExactlyOneDocument()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = ParseStdoutAsSingleJsonDocument(stdout);
        Assert.True(doc.RootElement.TryGetProperty("usage", out _));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task HostHelpWithoutJsonStaysHumanReadable()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["--help"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.ToString();
        Assert.Contains("Usage:", output);
        Assert.Contains("Commands:", output);
        Assert.DoesNotContain("{", output);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task HostUnknownCommandJsonEmitsErrorEnvelopeOnStdout()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["bogus", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.UsageError, exit);

        using var doc = ParseStdoutAsSingleJsonDocument(stdout);
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(CliErrorCode.UsageError, error.GetProperty("code").GetString());
        Assert.Contains("unknown command", error.GetProperty("message").GetString()!.ToLowerInvariant());

        // Human diagnostics — the error line and the help text — remain on stderr.
        var stderrContent = stderr.ToString();
        Assert.Contains("error:", stderrContent);
        Assert.Contains("Usage:", stderrContent);
        Assert.DoesNotContain("{\"error\"", stderrContent);
    }

    [Fact]
    public async Task HostUnknownCommandWithoutJsonKeepsStdoutEmpty()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["bogus"], stdout, stderr, provider);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Empty(stdout.ToString());
        Assert.Contains("Usage:", stderr.ToString());
    }

    [Fact]
    public async Task HostUnknownCommandJsonIgnoresQuietForErrorDocument()
    {
        // A failure is essential output, not suppressible progress: --quiet must not
        // empty the machine contract.
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["bogus", "--json", "--quiet"], stdout, stderr, provider);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = ParseStdoutAsSingleJsonDocument(stdout);
        Assert.Equal(CliErrorCode.UsageError, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("error:", stderr.ToString());
    }

    [Fact]
    public async Task HostRuntimeFailureJsonEmitsErrorEnvelopeOnStdout()
    {
        // A provider without ISourceAdapter makes the doctor factory throw (exit 1).
        var provider = new ServiceCollection().BuildServiceProvider();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["doctor", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = ParseStdoutAsSingleJsonDocument(stdout);
        Assert.Equal(CliErrorCode.Failure, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("error:", stderr.ToString());
    }

    [Fact]
    public async Task HostCancellationJsonEmitsErrorEnvelopeOnStdout()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exit = await CliHost.RunAsync(["doctor", "--json"], stdout, stderr, provider, cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        using var doc = ParseStdoutAsSingleJsonDocument(stdout);
        Assert.Equal(CliErrorCode.Cancelled, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("cancel", stderr.ToString().ToLowerInvariant());
    }

    // ---- Doctor command: stdout/stderr separation ----

    [Fact]
    public async Task DoctorWritesProgressToStderrAndResultToStdout()
    {
        var adapter = new StubSourceAdapter();
        var store = new StubArchiveStore();
        var command = new DoctorCommand(adapter, store);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, GlobalOptions.Default);

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);

        var stderrContent = stderr.ToString();
        var stdoutContent = stdout.ToString();

        // Progress goes to stderr.
        Assert.Contains("Checking source", stderrContent);
        Assert.Contains("Checking archive", stderrContent);

        // The result goes to stdout, not stderr.
        Assert.DoesNotContain("Checking source", stdoutContent);
    }

    [Fact]
    public async Task DoctorJsonEmitsSingleDocumentToStdout()
    {
        var adapter = new StubSourceAdapter();
        var store = new StubArchiveStore();
        var command = new DoctorCommand(adapter, store);
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);

        var output = stdout.ToString().TrimEnd();
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("ready").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("source").GetProperty("available").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("archive").GetProperty("available").GetBoolean());
    }

    // ---- --quiet ----

    [Fact]
    public async Task QuietSuppressesProgressOnStderr()
    {
        var adapter = new StubSourceAdapter();
        var store = new StubArchiveStore();
        var command = new DoctorCommand(adapter, store);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Quiet = true });

        await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task QuietDoesNotSuppressStdoutJson()
    {
        var adapter = new StubSourceAdapter();
        var store = new StubArchiveStore();
        var command = new DoctorCommand(adapter, store);
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null,
            new GlobalOptions { Json = true, Quiet = true });

        await command.ExecuteAsync(context, [], CancellationToken.None);

        var output = stdout.ToString().TrimEnd();
        Assert.NotEmpty(output);
        using var doc = JsonDocument.Parse(output);
        Assert.True(doc.RootElement.GetProperty("ready").GetBoolean());
    }

    // ---- --no-input ----

    [Fact]
    public async Task NoInputAcceptedByVersionCommand()
    {
        var command = new VersionCommand();
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null,
            new GlobalOptions { NoInput = true, Json = true });

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.NotEmpty(stdout.ToString());
    }

    [Fact]
    public void FailIfNoInputThrowsWhenNoInputIsSet()
    {
        var context = new CliContext(
            TextWriter.Null, TextWriter.Null,
            new GlobalOptions { NoInput = true });

        Assert.Throws<CliUsageException>(() => context.FailIfNoInput("prompt required"));
    }

    [Fact]
    public void FailIfNoInputDoesNotThrowWithoutNoInput()
    {
        var context = new CliContext(
            TextWriter.Null, TextWriter.Null,
            GlobalOptions.Default);

        context.FailIfNoInput("prompt required"); // must not throw
    }

    // ---- Exit codes ----

    [Fact]
    public async Task ExitTwoOnUnknownCommand()
    {
        var provider = BuildProvider(new StubSourceAdapter(), new StubArchiveStore());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["bogus", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.UsageError, exit);
        Assert.Contains("unknown command", stderr.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task ExitOneOnRuntimeFailure()
    {
        // A provider without ISourceAdapter makes the doctor factory throw,
        // which CliHost maps to exit 1.
        var provider = new ServiceCollection().BuildServiceProvider();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["doctor"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Failure, exit);
        Assert.Contains("error:", stderr.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task Exit130OnCancellation()
    {
        var adapter = new StubSourceAdapter();
        var store = new StubArchiveStore();
        var provider = BuildProvider(adapter, store);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var exit = await CliHost.RunAsync(["doctor"], stdout, stderr, provider, cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        Assert.Contains("cancel", stderr.ToString().ToLowerInvariant());
    }

    // ---- Doctor resilience ----

    [Fact]
    public async Task DoctorReportsUnavailableSourceWithoutCrashing()
    {
        var adapter = new StubSourceAdapter { ThrowOnDescribe = true };
        var store = new StubArchiveStore();
        var command = new DoctorCommand(adapter, store);
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit); // doctor is a health check; it reports, not crashes
        var output = stdout.ToString().TrimEnd();
        using var doc = JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("source").GetProperty("available").GetBoolean());
    }

    // ---- Helpers and stubs ----

    /// <summary>
    /// Asserts stdout carries exactly one JSON document: no trailing prose, no extra
    /// lines and no ANSI decoration (docs/ARCHITECTURE.md section 3.1.1).
    /// </summary>
    private static JsonDocument ParseStdoutAsSingleJsonDocument(StringWriter stdout)
    {
        var output = stdout.ToString().TrimEnd();
        Assert.NotEmpty(output);

        // Ordinal char checks: a culture-sensitive substring search treats ESC as an
        // ignorable character and would silently match unrelated text.
        Assert.False(output.Contains('\n'), "stdout must contain exactly one document, not multiple lines");
        Assert.False(output.Contains('\u001b'), "stdout must not carry ANSI escape decoration");
        return JsonDocument.Parse(output);
    }

    private static ServiceProvider BuildProvider(ISourceAdapter adapter, IArchiveStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(adapter);
        services.AddSingleton(store);
        // The discovery commands (account, conversation) are constructed while rendering help,
        // so the minimal test provider must expose the catalog service just as AddWeArchiveCore
        // does in production. Otherwise DescribeHelp() would throw while reading descriptions.
        services.AddSingleton(sp => new SourceCatalogService(sp.GetRequiredService<ISourceAdapter>()));
        return services.BuildServiceProvider();
    }

    /// <summary>Minimal source adapter stub for doctor tests.</summary>
    private sealed class StubSourceAdapter : ISourceAdapter
    {
        public bool ThrowOnDescribe { get; set; }

        public string AdapterName => "stub";
        public string AdapterVersion => "1.0.0";

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken)
        {
            if (ThrowOnDescribe)
                throw new InvalidOperationException("source unreachable");
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                IsAvailable = true,
                SourceVersion = "4.1.0",
                SourceProductName = "Stub",
            });
        }

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    /// <summary>Minimal archive store stub for doctor tests.</summary>
    private sealed class StubArchiveStore : IArchiveStore
    {
        public string ArchivePath => "<stub>";

        public Task InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<ArchiveStats> GetArchiveStatsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ArchiveStats
            {
                AccountCount = 1,
                ConversationCount = 2,
                ParticipantCount = 3,
                MessageCount = 100,
                ArchivePath = "<stub>",
            });
        }

        public Task<ImportRun> BeginImportRunAsync(
            string accountId, SourceDescriptor descriptor, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task CompleteImportRunAsync(ImportRun run, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IConversationImportSession> BeginConversationImportAsync(
            ArchiveConversation conversation, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task UpsertAccountAsync(ArchiveAccount account, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task UpsertParticipantsAsync(
            IEnumerable<ArchiveParticipant> participants, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task UpsertConversationsAsync(
            IEnumerable<ArchiveConversation> conversations, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<UpsertCounters> UpsertMessagesAsync(
            IReadOnlyList<CanonicalMessage> messages, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ArchiveAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ArchiveConversation>> ListConversationsAsync(
            string accountId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ArchiveConversation?> GetConversationAsync(
            string conversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ArchiveParticipant>> ListParticipantsAsync(
            string accountId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<CanonicalMessage>> ReadMessagesAsync(
            string conversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<int> ResolveReplyTargetsAsync(string conversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ArchiveMessagePage> QueryMessagesAsync(
            ArchiveMessageQuery query, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ArchiveMessageContext?> ReadMessageContextAsync(
            string messageId, int before, int after, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<IngestFreshness>> ListIngestFreshnessAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<ConversationStats> GetConversationStatsAsync(
            string conversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IngestCheckpoint?> GetIngestCheckpointAsync(string accountId, string adapterFamily, string scopeKind, string scopeId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task SetIngestCheckpointAsync(IngestCheckpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
