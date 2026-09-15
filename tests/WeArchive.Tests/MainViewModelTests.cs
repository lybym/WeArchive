using WeArchive.App.Services;
using WeArchive.App.ViewModels;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Domain;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.Settings;
using WeArchive.Infrastructure.WeChat;
using WeArchive.Tests.Support;

namespace WeArchive.Tests;

/// <summary>
/// Verifies presentation-layer lifecycle correctness for the fire-and-forget
/// conversation-list load started from the <see cref="MainViewModel.SelectedAccount"/> setter:
/// recoverable listing failures are observed and surfaced as a user-facing status (never an
/// unobserved exception / crash log), and a list load superseded by a later account
/// selection is discarded so the UI never shows one account's conversations while an export
/// uses another account's <c>SourceProfileId</c>. docs/ARCHITECTURE.md sections 3.1 and 11.
/// </summary>
public sealed class MainViewModelTests
{
    /// <summary>
    /// An adapter whose conversation listing throws the same exception the real WeChat
    /// adapter raises when the client is not running, while discovery and account listing
    /// still succeed from on-disk data — exactly the preview's missing-client condition.
    /// </summary>
    private sealed class ConversationListFailingAdapter : ISourceAdapter
    {
        public const string ProfileId = "stub_account";

        public string AdapterName => "stub";
        public string AdapterVersion => "1.0.0";

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                SourceProductName = "stub source",
                IsAvailable = true,
            });

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceAccount>>(
            [
                new SourceAccount
                {
                    SourceProfileId = ProfileId,
                    DisplayName = "stub account",
                    IsCurrent = true,
                    LastActiveAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                },
            ]);

        // Mirrors WeChatWindowsSourceAdapter.GetCache() throwing when Weixin.exe is not
        // running: the call faults synchronously inside the background Task.Run.
        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            throw new WeChatKeyUnavailableException(
                "The WeChat client process (Weixin.exe) is not running. Start WeChat and sign in, then retry.");

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            throw new WeChatKeyUnavailableException(
                "The WeChat client process (Weixin.exe) is not running.");

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceParticipant>>([]);

        public async IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId,
            string sourceConversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    /// <summary>
    /// A source adapter whose conversation listing for each profile is gated by a
    /// <see cref="TaskCompletionSource{TResult}"/> the test completes manually, so an earlier
    /// account's load can be made to complete after a later selection's. Used to verify that
    /// a stale, out-of-order list load never publishes or persists the wrong account.
    /// </summary>
    private sealed class SequencedListAdapter : ISourceAdapter
    {
        private readonly Dictionary<string, SourceAccount> _accounts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskCompletionSource<IReadOnlyList<SourceConversation>>> _gates =
            new(StringComparer.OrdinalIgnoreCase);

        public string AdapterName => "sequenced";
        public string AdapterVersion => "1.0.0";

        public void AddAccount(string profileId)
        {
            _accounts[profileId] = new SourceAccount
            {
                SourceProfileId = profileId,
                DisplayName = profileId,
                IsCurrent = true,
                LastActiveAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            };

            _gates[profileId] = new TaskCompletionSource<IReadOnlyList<SourceConversation>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Complete(string profileId, params string[] conversationIds) =>
            _gates[profileId].SetResult(conversationIds
                .Select(id => new SourceConversation
                {
                    SourceConversationId = id,
                    Kind = ConversationKind.Direct,
                    Title = id,
                })
                .ToList());

        public Task<SourceDescriptor> DescribeSourceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceDescriptor
            {
                AdapterName = AdapterName,
                AdapterVersion = AdapterVersion,
                SourceProductName = "sequenced source",
                IsAvailable = true,
            });

        public Task<IReadOnlyList<SourceAccount>> ListAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceAccount>>(_accounts.Values.ToList());

        public Task<IReadOnlyList<SourceConversation>> ListConversationsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            _gates[sourceProfileId].Task;

        public Task<SourceConversationDetail> DescribeConversationAsync(
            string sourceProfileId, string sourceConversationId, CancellationToken cancellationToken) =>
            throw new NotImplementedException(
                "DescribeConversationAsync is not used by the conversation-list-load tests.");

        public Task<IReadOnlyList<SourceParticipant>> ListParticipantsAsync(
            string sourceProfileId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SourceParticipant>>([]);

        public async IAsyncEnumerable<SourceMessage> ReadMessagesAsync(
            string sourceProfileId,
            string sourceConversationId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class StubFolderPicker : IFolderPicker
    {
        public string? PickFolder(string? initialDirectory, string title) => null;
    }

    private static (MainViewModel ViewModel, SettingsStore Settings) CreateViewModel(
        TempDirectory temp,
        ISourceAdapter catalogAdapter)
    {
        // A fully valid archive workflow is constructed so the view model has non-null
        // collaborators; the conversation-list path under test never reaches it.
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new JsonlDatasetExporter(store);
        var fixtureCatalog = new SourceCatalogService(new FixtureSourceAdapter());
        var importer = new ImportService(new FixtureSourceAdapter(), store, clock);
        var workflow = new ArchiveWorkflow(fixtureCatalog, importer, exporter, store, clock);

        var catalog = new SourceCatalogService(catalogAdapter);
        var settings = new SettingsStore(temp.Combine("settings.json"));
        var updates = new UpdateService("https://github.com/lybym/WeArchive");
        var folderPicker = new StubFolderPicker();

        var viewModel = new MainViewModel(catalog, workflow, settings, updates, folderPicker);
        return (viewModel, settings);
    }

    [Fact]
    public async Task AConversationListFailureIsObservedAndSurfacedAsStatusNotAnUnobservedException()
    {
        using var temp = new TempDirectory();
        var (viewModel, _) = CreateViewModel(temp, new ConversationListFailingAdapter());

        // Reproduce the preview trigger: an account becomes selected (as source discovery +
        // account listing set it on startup), which fire-and-forgets the conversation-list
        // load against a source whose key is unavailable.
        viewModel.Accounts.Add(new SourceAccount
        {
            SourceProfileId = ConversationListFailingAdapter.ProfileId,
            DisplayName = "stub account",
            IsCurrent = true,
        });
        viewModel.SelectedAccount = viewModel.Accounts[0];

        // The load observes its own exception: it must complete, not fault.
        var load = viewModel.ConversationLoadTask;
        Assert.NotNull(load);
        await load.ConfigureAwait(true);

        // The failure is surfaced to the user as a conversation-list status, and no
        // conversations are presented as if the list had loaded.
        Assert.Empty(viewModel.Conversations);
        Assert.True(viewModel.HasConversationStatus);
        Assert.Contains("Weixin.exe", viewModel.ConversationStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleAccountLoadCompletingAfterALaterSelectionIsDiscarded()
    {
        using var temp = new TempDirectory();
        var adapter = new SequencedListAdapter();
        adapter.AddAccount("account_a");
        adapter.AddAccount("account_b");
        // B's list resolves immediately when its load starts; A's stays parked until the test
        // completes it, so A completes out of order after B.
        adapter.Complete("account_b", "b-conv-1");

        var (viewModel, settings) = CreateViewModel(temp, adapter);

        var accountA = new SourceAccount
        {
            SourceProfileId = "account_a",
            DisplayName = "A",
            IsCurrent = true,
        };
        var accountB = new SourceAccount
        {
            SourceProfileId = "account_b",
            DisplayName = "B",
            IsCurrent = true,
        };
        viewModel.Accounts.Add(accountA);
        viewModel.Accounts.Add(accountB);

        // Select A: its load starts and parks on A's unresolved gate.
        viewModel.SelectedAccount = accountA;
        var aLoad = viewModel.ConversationLoadTask;
        Assert.NotNull(aLoad);
        Assert.False(aLoad.IsCompleted);

        // While A's load is still in flight, select B. B's load resolves immediately,
        // publishes B's conversations and persists B's profile id.
        viewModel.SelectedAccount = accountB;
        var bLoad = viewModel.ConversationLoadTask;
        Assert.NotNull(bLoad);
        await bLoad.ConfigureAwait(true);

        Assert.Single(viewModel.Conversations);
        Assert.Equal("b-conv-1", viewModel.Conversations[0].SourceConversationId);
        Assert.Equal("account_b", settings.Load().SourceProfileId);

        // Now A's slow load completes out of order. It must be discarded: A's conversations
        // must not appear, and A's profile id must not overwrite B's persisted selection.
        adapter.Complete("account_a", "a-conv-1", "a-conv-2");
        await aLoad.ConfigureAwait(true);

        Assert.Single(viewModel.Conversations);
        Assert.Equal("b-conv-1", viewModel.Conversations[0].SourceConversationId);
        Assert.Equal("account_b", settings.Load().SourceProfileId);
    }
}
