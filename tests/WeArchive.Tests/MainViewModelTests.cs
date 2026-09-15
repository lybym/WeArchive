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
/// Verifies that the presentation layer observes conversation-listing failures itself and
/// surfaces them as a user-facing status, instead of letting them escape a fire-and-forget
/// task as an unobserved exception that writes a crash log. This is the regression for the
/// preview failure where a missing WeChat client (a normal precondition) produced an
/// unobserved <c>AggregateException</c> / <c>WeChatKeyUnavailableException</c> and crash
/// logs at startup. docs/ARCHITECTURE.md sections 3.1 and 11.
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

    private sealed class StubFolderPicker : IFolderPicker
    {
        public string? PickFolder(string? initialDirectory, string title) => null;
    }

    private static MainViewModel CreateViewModel(TempDirectory temp)
    {
        // A fully valid archive workflow is constructed so the view model has non-null
        // collaborators; the conversation-list path under test never reaches it.
        var clock = new FixedClock();
        var store = new SqliteArchiveStore(temp.Combine("archive.db"), clock);
        var exporter = new JsonlDatasetExporter(store);
        var fixtureCatalog = new SourceCatalogService(new FixtureSourceAdapter());
        var importer = new ImportService(new FixtureSourceAdapter(), store, clock);
        var workflow = new ArchiveWorkflow(fixtureCatalog, importer, exporter, store, clock);

        var catalog = new SourceCatalogService(new ConversationListFailingAdapter());
        var settings = new SettingsStore(temp.Combine("settings.json"));
        var updates = new UpdateService("https://github.com/lybym/WeArchive");
        var folderPicker = new StubFolderPicker();

        return new MainViewModel(catalog, workflow, settings, updates, folderPicker);
    }

    [Fact]
    public async Task AConversationListFailureIsObservedAndSurfacedAsStatusNotAnUnobservedException()
    {
        using var temp = new TempDirectory();
        var viewModel = CreateViewModel(temp);

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
}
