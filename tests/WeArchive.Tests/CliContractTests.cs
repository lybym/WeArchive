using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Cli.Output.Dto;

namespace WeArchive.Tests;

/// <summary>
/// Contract tests for the CLI foundation: command parsing, JSON shape, stdout/stderr
/// separation, --quiet, --no-input and exit-code mapping.
/// docs/PRD.md FR-22, docs/DEVELOPMENT.md section 7 "CLI contract tests".
/// </summary>
public sealed class CliContractTests
{
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
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("version").GetString());
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

        // No CSI escape sequences (the real ANSI decoration pattern).
        Assert.DoesNotContain("\u001b[", output);
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
        Assert.Equal("0.1.0", doc.RootElement.GetProperty("version").GetString());
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

    private static ServiceProvider BuildProvider(ISourceAdapter adapter, IArchiveStore store)
    {
        var services = new ServiceCollection();
        services.AddSingleton(adapter);
        services.AddSingleton(store);
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

        public Task<ConversationStats> GetConversationStatsAsync(
            string conversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
