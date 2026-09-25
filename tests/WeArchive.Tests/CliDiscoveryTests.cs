using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Cli.CommandLine;
using WeArchive.Cli.Commands;
using WeArchive.Cli.Output.Dto;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;

namespace WeArchive.Tests;

/// <summary>
/// Discovery command contract tests for <c>wearchive account list</c>,
/// <c>wearchive conversation list</c> and <c>wearchive conversation show</c> (Issue #7 / M0.5).
/// docs/PRD.md FR-01/FR-03/FR-22, docs/DEVELOPMENT.md section 7 "CLI contract tests".
/// <para>
/// These exercise the commands as thin adapters over <see cref="SourceCatalogService"/> with an
/// in-memory fake source adapter, asserting stable identifiers (mirroring
/// <see cref="StableIds.Conversation"/>), source-neutral JSON, stdout/stderr separation, exit
/// codes and structured failure diagnostics.
/// </para>
/// </summary>
public sealed class CliDiscoveryTests
{
    // ---- account list ------------------------------------------------------

    [Fact]
    public async Task AccountListJsonEmitsStableIdsAndMultipleAccounts()
    {
        var adapter = FakeAdapter();
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());

        var first = root[0];
        Assert.Equal("wxid_alice", first.GetProperty("source_profile_id").GetString());
        Assert.Equal(
            StableIds.Account(adapter.AdapterName, "wxid_alice"),
            first.GetProperty("stable_id").GetString());
        Assert.True(first.GetProperty("is_current").GetBoolean());

        // Only documented, source-neutral fields appear — no raw source table/type details.
        var names = first.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.True(names.SetEquals(new HashSet<string>
        {
            "source_profile_id", "stable_id", "display_name",
            "is_current", "last_active_at", "data_root_path",
        }));
    }

    [Fact]
    public async Task AccountListHumanIsNotJson()
    {
        var adapter = FakeAdapter();
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, GlobalOptions.Default);

        await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        var output = stdout.ToString();
        Assert.Contains("Accounts (2)", output);
        Assert.Contains("wxid_alice", output);
        Assert.Contains(
            StableIds.Account(adapter.AdapterName, "wxid_alice"),
            output);
        Assert.DoesNotContain("{", output);
    }

    [Fact]
    public async Task AccountListEmptySourceEmitsEmptyArrayAndExitsZero()
    {
        var adapter = FakeAdapter();
        adapter.Accounts.Clear();
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.Equal("[]", stdout.ToString().TrimEnd());
    }

    [Fact]
    public async Task AccountListSourceUnavailableExitsOneWithStructuredError()
    {
        var adapter = FakeAdapter();
        adapter.ListAccountsException = new InvalidOperationException("source unreachable");
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.SourceUnavailable,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("error:", stderr.ToString());
    }

    [Fact]
    public async Task AccountListRequiresListSubcommand()
    {
        var adapter = FakeAdapter();
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.UsageError,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- conversation list -------------------------------------------------

    [Fact]
    public async Task ConversationListJsonStableIdsMatchArchiveDerivation()
    {
        var adapter = FakeAdapter();
        var accountId = StableIds.Account(adapter.AdapterName, "wxid_alice");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);

        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(4, root.GetArrayLength());

        foreach (var item in root.EnumerateArray())
        {
            var sourceId = item.GetProperty("source_id").GetString()!;
            var kindWire = item.GetProperty("kind").GetString()!;
            var kind = ParseKind(kindWire);
            var peer = item.GetProperty("peer_source_user_id").ValueKind == JsonValueKind.Null
                ? null
                : item.GetProperty("peer_source_user_id").GetString();

            // The stable_id must be byte-identical to the documented derivation used by import.
            Assert.Equal(
                StableIds.Conversation(accountId, kind, sourceId, peer),
                item.GetProperty("stable_id").GetString());
        }

        // kind is a stable wire name, never a raw numeric type code.
        var kinds = root.EnumerateArray()
            .Select(c => c.GetProperty("kind").GetString()!)
            .ToHashSet();
        Assert.Subset(
            new HashSet<string> { "direct", "group", "official", "system", "unknown" },
            kinds);

        // No raw source implementation details leak into the JSON.
        var forbidden = new HashSet<string> { "source_type", "source_subtype", "table", "type_code", "raw" };
        foreach (var item in root.EnumerateArray())
        {
            var names = item.EnumerateObject().Select(p => p.Name).ToHashSet();
            foreach (var forbiddenName in forbidden)
                Assert.DoesNotContain(forbiddenName, names);
        }
    }

    [Fact]
    public async Task ConversationListHumanShowsAccountAndConversations()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, GlobalOptions.Default);

        await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        var output = stdout.ToString();
        Assert.Contains("Conversations (4)", output);
        Assert.Contains("account: wxid_alice", output);
        Assert.DoesNotContain("{", output);
    }

    [Fact]
    public async Task ConversationListAcceptsAccountByStableId()
    {
        var adapter = FakeAdapter();
        var bobStableId = StableIds.Account(adapter.AdapterName, "wxid_bob");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list", "--account", bobStableId], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        // Bob has one conversation (the official account).
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("gh_official_bob", doc.RootElement[0].GetProperty("source_id").GetString());
    }

    [Fact]
    public async Task ConversationListAcceptsAccountBySourceProfileId()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list", "--account", "wxid_bob"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ConversationListAccountEqualsFormIsAccepted()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(
            context, ["list", "--account=wxid_bob"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(1, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task ConversationListUnknownAccountExitsOneWithAccountNotFound()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(
            context, ["list", "--account", "wxid_nope"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.AccountNotFound,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationListNoAccountsExitsOneWithNoAccounts()
    {
        var adapter = FakeAdapter();
        adapter.Accounts.Clear();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.NoAccounts,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationListSourceUnavailableExitsOne()
    {
        var adapter = FakeAdapter();
        adapter.ListAccountsException = new InvalidOperationException("source unreachable");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.SourceUnavailable,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationListListingFailureExitsOneWithConversationListFailed()
    {
        var adapter = FakeAdapter();
        adapter.ListConversationsException = new InvalidOperationException("listing failed");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.ConversationListFailed,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationListRejectsUnexpectedPositional()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["list", "extra"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.UsageError,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- conversation show -------------------------------------------------

    [Fact]
    public async Task ConversationShowByStableArchiveIdReturnsDetail()
    {
        var adapter = FakeAdapter();
        var accountId = StableIds.Account(adapter.AdapterName, "wxid_alice");
        var groupStableId = StableIds.Conversation(
            accountId, ConversationKind.Group, "100200300@chatroom", peerSourceUserId: null);
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["show", groupStableId], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        var root = doc.RootElement;
        Assert.Equal(groupStableId, root.GetProperty("stable_id").GetString());
        Assert.Equal("100200300@chatroom", root.GetProperty("source_id").GetString());
        Assert.Equal("group", root.GetProperty("kind").GetString());
        Assert.Equal(24, root.GetProperty("message_count").GetInt32());
        Assert.Equal(5, root.GetProperty("participant_count").GetInt32());
    }

    [Fact]
    public async Task ConversationShowBySourceIdReturnsDetail()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(
            context, ["show", "100200300@chatroom"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal("100200300@chatroom", doc.RootElement.GetProperty("source_id").GetString());
        Assert.Equal("group", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ConversationShowDirectConversationUsesPeerStableId()
    {
        var adapter = FakeAdapter();
        var accountId = StableIds.Account(adapter.AdapterName, "wxid_alice");
        var directStableId = StableIds.Conversation(
            accountId, ConversationKind.Direct, "wxid_carol", "wxid_carol");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["show", directStableId], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(directStableId, doc.RootElement.GetProperty("stable_id").GetString());
        Assert.Equal("wxid_carol", doc.RootElement.GetProperty("peer_source_user_id").GetString());
    }

    [Fact]
    public async Task ConversationShowHumanIsNotJson()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, GlobalOptions.Default);

        await command.ExecuteAsync(context, ["show", "100200300@chatroom"], CancellationToken.None);

        var output = stdout.ToString();
        Assert.Contains("Conversation:", output);
        Assert.Contains("stable id:", output);
        Assert.Contains("100200300@chatroom", output);
        Assert.DoesNotContain("{", output);
    }

    [Fact]
    public async Task ConversationShowNotFoundExitsOneWithConversationNotFound()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["show", "g_doesnotexist"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.ConversationNotFound,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationShowDescribeFailureExitsOneWithConversationDescribeFailed()
    {
        var adapter = FakeAdapter();
        adapter.DescribeConversationException = new InvalidOperationException("describe failed");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["show", "100200300@chatroom"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.ConversationDescribeFailed,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationShowListingFailureDuringResolutionExitsOne()
    {
        var adapter = FakeAdapter();
        adapter.ListConversationsException = new InvalidOperationException("listing failed");
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["show", "100200300@chatroom"], CancellationToken.None);

        Assert.Equal(ExitCode.Failure, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.ConversationListFailed,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationShowRequiresAnIdentifier()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, ["show"], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.UsageError,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ConversationRequiresSubcommand()
    {
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null, new GlobalOptions { Json = true });

        var exit = await command.ExecuteAsync(context, [], CancellationToken.None);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.UsageError,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- stdout/stderr separation, quiet, no-input, cancellation -----------

    [Fact]
    public async Task JsonSuppressesProgressOnStderr()
    {
        // --json suppresses progress so machine stdout is never interleaved with it on a tty.
        var adapter = FakeAdapter();
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Json = true });

        await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Empty(stderr.ToString());
        Assert.NotEmpty(stdout.ToString());
    }

    [Fact]
    public async Task QuietSuppressesProgressOnStderr()
    {
        var adapter = FakeAdapter();
        var command = new AccountCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var context = new CliContext(stdout, stderr, new GlobalOptions { Quiet = true });

        await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task NoInputNeverPromptsForDiscovery()
    {
        // Discovery is read-only and resolves deterministically; --no-input must be accepted.
        var adapter = FakeAdapter();
        var command = new ConversationCommand(new SourceCatalogService(adapter));
        var stdout = new StringWriter();
        var context = new CliContext(stdout, TextWriter.Null,
            new GlobalOptions { NoInput = true, Json = true });

        var exit = await command.ExecuteAsync(context, ["list"], CancellationToken.None);

        Assert.Equal(ExitCode.Success, exit);
        Assert.NotEmpty(stdout.ToString());
    }

    [Fact]
    public async Task HostAccountListJsonEmitsSingleDocument()
    {
        var adapter = FakeAdapter();
        var provider = BuildProvider(adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["account", "list", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        var output = stdout.ToString().TrimEnd();
        Assert.False(output.Contains('\n'), "stdout must be exactly one document");
        Assert.Empty(stderr.ToString()); // --json suppresses progress
    }

    [Fact]
    public async Task HostConversationListRoutingAndExitZero()
    {
        var adapter = FakeAdapter();
        var provider = BuildProvider(adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(
            ["conversation", "list", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(4, doc.RootElement.GetArrayLength());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task HostConversationShowRoutingAndExitZero()
    {
        var adapter = FakeAdapter();
        var provider = BuildProvider(adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(
            ["conversation", "show", "100200300@chatroom", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal("group", doc.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task HostHelpListsDiscoveryCommands()
    {
        var adapter = FakeAdapter();
        var provider = BuildProvider(adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(["--help", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.Success, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        var commands = doc.RootElement.GetProperty("commands")
            .EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()!)
            .ToList();
        Assert.Contains("account", commands);
        Assert.Contains("conversation", commands);
    }

    [Fact]
    public async Task HostCancellationExits130()
    {
        var adapter = FakeAdapter();
        var provider = BuildProvider(adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exit = await CliHost.RunAsync(
            ["account", "list", "--json"], stdout, stderr, provider, cts.Token);

        Assert.Equal(ExitCode.Cancelled, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.Cancelled,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task HostUnknownSubcommandExitsTwo()
    {
        var adapter = FakeAdapter();
        var provider = BuildProvider(adapter);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliHost.RunAsync(
            ["conversation", "bogus", "--json"], stdout, stderr, provider);

        Assert.Equal(ExitCode.UsageError, exit);
        using var doc = JsonDocument.Parse(stdout.ToString().TrimEnd());
        Assert.Equal(
            CliErrorCode.UsageError,
            doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- helpers and fake adapter ------------------------------------------

    private static ServiceProvider BuildProvider(FakeDiscoveryAdapter adapter)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISourceAdapter>(adapter);
        services.AddSingleton<IArchiveStore>(new StubArchiveStore());
        services.AddSingleton<SourceCatalogService>(
            sp => new SourceCatalogService(sp.GetRequiredService<ISourceAdapter>()));
        return services.BuildServiceProvider();
    }

    private static ConversationKind ParseKind(string wire) => wire switch
    {
        "direct" => ConversationKind.Direct,
        "group" => ConversationKind.Group,
        "official" => ConversationKind.Official,
        "system" => ConversationKind.System,
        _ => ConversationKind.Unknown,
    };

    /// <summary>
    /// Builds a fake source adapter with two accounts and a mix of conversation kinds so the
    /// stable-id derivation (group vs non-group, peer fallback) is exercised. The data is
    /// synthetic and contains no real chat content.
    /// </summary>
    private static FakeDiscoveryAdapter FakeAdapter()
    {
        var adapter = new FakeDiscoveryAdapter();
        adapter.Accounts.Add(new SourceAccount
        {
            SourceProfileId = "wxid_alice",
            DisplayName = "Alice",
            IsCurrent = true,
            LastActiveAt = new DateTimeOffset(2026, 2, 24, 9, 0, 0, TimeSpan.FromHours(8)),
        });
        adapter.Accounts.Add(new SourceAccount
        {
            SourceProfileId = "wxid_bob",
            DisplayName = "Bob",
            IsCurrent = false,
            LastActiveAt = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.FromHours(8)),
        });

        var aliceConversations = new List<SourceConversation>
        {
            new()
            {
                SourceConversationId = "100200300@chatroom",
                Kind = ConversationKind.Group,
                Title = "Workgroup",
                PeerSourceUserId = null,
                LastMessageAt = new DateTimeOffset(2026, 2, 24, 9, 0, 0, TimeSpan.FromHours(8)),
                MessageCountHint = 24,
            },
            new()
            {
                // Direct conversation whose peer is known.
                SourceConversationId = "wxid_carol",
                Kind = ConversationKind.Direct,
                Title = null,
                PeerSourceUserId = "wxid_carol",
                LastMessageAt = new DateTimeOffset(2026, 2, 20, 18, 30, 0, TimeSpan.FromHours(8)),
                MessageCountHint = 10,
            },
            new()
            {
                // Direct conversation with no peer: stable id falls back to the source id.
                SourceConversationId = "direct-session-456",
                Kind = ConversationKind.Direct,
                Title = null,
                PeerSourceUserId = null,
                LastMessageAt = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.FromHours(8)),
                MessageCountHint = 3,
            },
            new()
            {
                // Official account: non-group, so it derives like a direct conversation.
                SourceConversationId = "gh_official_alice",
                Kind = ConversationKind.Official,
                Title = "Announcements",
                PeerSourceUserId = null,
                LastMessageAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
                MessageCountHint = null,
            },
        };
        adapter.Conversations["wxid_alice"] = aliceConversations;

        adapter.Conversations["wxid_bob"] = new List<SourceConversation>
        {
            new()
            {
                SourceConversationId = "gh_official_bob",
                Kind = ConversationKind.Official,
                Title = "Bob News",
                PeerSourceUserId = null,
                LastMessageAt = new DateTimeOffset(2026, 1, 9, 10, 0, 0, TimeSpan.FromHours(8)),
                MessageCountHint = 2,
            },
        };

        var first = new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.FromHours(8));
        var last = new DateTimeOffset(2026, 2, 24, 9, 0, 0, TimeSpan.FromHours(8));
        adapter.Details[("wxid_alice", "100200300@chatroom")] = new SourceConversationDetail
        {
            SourceConversationId = "100200300@chatroom",
            MessageCount = 24,
            FirstMessageAt = first,
            LastMessageAt = last,
            ParticipantCount = 5,
        };
        adapter.Details[("wxid_alice", "wxid_carol")] = new SourceConversationDetail
        {
            SourceConversationId = "wxid_carol",
            MessageCount = 10,
            FirstMessageAt = first,
            LastMessageAt = last,
            ParticipantCount = 2,
        };
        adapter.Details[("wxid_alice", "direct-session-456")] = new SourceConversationDetail
        {
            SourceConversationId = "direct-session-456",
            MessageCount = 3,
            FirstMessageAt = first,
            LastMessageAt = last,
            ParticipantCount = 2,
        };
        adapter.Details[("wxid_alice", "gh_official_alice")] = new SourceConversationDetail
        {
            SourceConversationId = "gh_official_alice",
            MessageCount = 0,
            FirstMessageAt = null,
            LastMessageAt = last,
            ParticipantCount = 1,
        };

        return adapter;
    }

    /// <summary>
    /// In-memory fake source adapter for discovery tests. It is read-only, deterministic and
    /// contains no real chat content. Per-method exceptions simulate source-unavailable and
    /// listing/describe failures.
    /// </summary>
    private sealed class FakeDiscoveryAdapter : ISourceAdapter
    {
        public string AdapterName => "wechat-windows";
        public string AdapterVersion => "0.1.0";

        public List<SourceAccount> Accounts { get; } = new();
        public Dictionary<string, List<SourceConversation>> Conversations { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Profile, string SourceId), SourceConversationDetail> Details { get; } = new();

        public Exception? ListAccountsException { get; set; }
        public Exception? ListConversationsException { get; set; }
        public Exception? DescribeConversationException { get; set; }

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                IsAvailable = true,
                SourceProductName = "Fake",
            });
        }

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListAccountsException is not null)
                throw ListAccountsException;
            return Task.FromResult<IReadOnlyList<SourceAccount>>(Accounts);
        }

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListConversationsException is not null)
                throw ListConversationsException;
            return Task.FromResult<IReadOnlyList<SourceConversation>>(
                Conversations.TryGetValue(sourceProfileId, out var list)
                    ? list
                    : Array.Empty<SourceConversation>());
        }

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DescribeConversationException is not null)
                throw DescribeConversationException;
            return Task.FromResult(Details[(sourceProfileId, sourceConversationId)]);
        }

        public IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    /// <summary>
    /// Minimal archive store stub so the CLI composition root (and the doctor command, which is
    /// constructed while rendering help) can be built for discovery tests. Discovery commands
    /// never execute doctor, so every method beyond construction stays unimplemented.
    /// </summary>
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
                AccountCount = 0,
                ConversationCount = 0,
                ParticipantCount = 0,
                MessageCount = 0,
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

        public Task<IngestCheckpoint?> GetIngestCheckpointAsync(string accountId, string adapterFamily, string scopeKind, string scopeId, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }
}
