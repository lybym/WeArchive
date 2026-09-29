using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Domain;
using WeArchive.Core.RawVault;
using WeArchive.Core.Services;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Tests.Support;

/// <summary>
/// The shared synthetic-WeChat synchronization environment for conversation-scoped
/// (<c>sync --conversation</c>) and Collection-scoped (<c>sync --collection</c>) tests: the real
/// composition root plus a synthetic WeChat capture source, so a sync runs the production path end
/// to end — selector resolution over the live surface, <see cref="CaptureService"/>, Raw Vault
/// publication and the real Raw Vault ingest — without a live client or a database key
/// (docs/PRD.md NFR-06).
/// <para>
/// The provider is built through <c>AddWeArchiveCore</c>, so these tests also cover the shipped
/// dependency wiring rather than a hand-assembled object graph.
/// </para>
/// </summary>
internal sealed class WeChatSyncHarness : IDisposable
{
    private WeChatSyncHarness(
        ServiceProvider provider,
        SyntheticWeChatSourceAdapter source,
        SyntheticWeChatCaptureAdapter capture,
        FixedClock clock,
        string archivePath,
        string configurationPath,
        string accountId)
    {
        Provider = provider;
        Source = source;
        Capture = capture;
        Clock = clock;
        ArchivePath = archivePath;
        ConfigurationPath = configurationPath;
        AccountId = accountId;
        Catalog = provider.GetRequiredService<CollectionCatalogService>();
        Sync = provider.GetRequiredService<CollectionSyncService>();
        ConversationSync = provider.GetRequiredService<ConversationSyncService>();
        SourceCatalog = provider.GetRequiredService<SourceCatalogService>();
        Archive = (SqliteArchiveStore)provider.GetRequiredService<IArchiveStore>();
        Vault = provider.GetRequiredService<IRawVaultStore>();
    }

    public ServiceProvider Provider { get; }

    public SyntheticWeChatSourceAdapter Source { get; }

    public SyntheticWeChatCaptureAdapter Capture { get; }

    /// <summary>The deterministic clock. Advance it between captures of one test.</summary>
    public FixedClock Clock { get; }

    public CollectionCatalogService Catalog { get; }

    public CollectionSyncService Sync { get; }

    public ConversationSyncService ConversationSync { get; }

    /// <summary>The live discovery surface the CLI's selector resolution reads.</summary>
    public SourceCatalogService SourceCatalog { get; }

    public SqliteArchiveStore Archive { get; }

    public IRawVaultStore Vault { get; }

    public string ArchivePath { get; }

    /// <summary>Absolute path of the authoritative Collection configuration file.</summary>
    public string ConfigurationPath { get; }

    public string AccountId { get; }

    public static string ProfileId => SyntheticWeChatSourceAdapter.ProfileId;

    public static WeChatSyncHarness Create(
        TempDirectory temp,
        params SyntheticCaptureConversation[] conversations) =>
        Create(temp, conversations, configure: null);

    /// <summary>
    /// Creates the environment and lets one test adjust the shipped DI graph before it is built
    /// (for example to wrap <c>IConversationIngestService</c> so a cancellation point is
    /// deterministic). Later explicit registrations win on resolution, exactly as the clock and
    /// synthetic source overrides do.
    /// </summary>
    public static WeChatSyncHarness Create(
        TempDirectory temp,
        SyntheticCaptureConversation[] conversations,
        Action<IServiceCollection>? configure)
    {
        ArgumentNullException.ThrowIfNull(temp);
        ArgumentNullException.ThrowIfNull(conversations);

        var source = new SyntheticWeChatSourceAdapter { Conversations = conversations };
        var capture = new SyntheticWeChatCaptureAdapter(conversations);
        var archivePath = temp.Combine("archive", "wearchive.db");
        var configurationPath = temp.Combine("collections.yaml");
        var clock = new FixedClock();
        var accountId = StableIds.Account(WeChatWindowsSourceAdapter.Name, SyntheticWeChatSourceAdapter.ProfileId);

        var services = new ServiceCollection();
        services.AddWeArchiveCore(archivePath, temp.Combine("rawvault"), configurationPath);
        // AddWeArchiveCore uses TryAddSingleton; later explicit registrations win on resolution,
        // so the deterministic clock and the synthetic source replace the production defaults.
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<ISourceAdapter>(source);
        services.AddSingleton<ISourceCaptureAdapter>(capture);
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        return new WeChatSyncHarness(provider, source, capture, clock, archivePath, configurationPath, accountId);
    }

    /// <summary>
    /// Moves the deterministic clock forward so a second capture in the same test publishes a new
    /// generation. Generation identity includes the capture instant, so two captures of one account
    /// at the same instant would collide by design (docs/DATA_MODEL.md section 21.1).
    /// </summary>
    public void AdvanceClock() => Clock.UtcNow = Clock.UtcNow.AddHours(1);

    /// <summary>The stable conversation id the source surface reports for an upstream id.</summary>
    public string StableId(string sourceConversationId) =>
        SyntheticWeChatNaming.StableConversationId(AccountId, sourceConversationId);

    public static string DirectId(string suffix) => SyntheticWeChatNaming.DirectId(suffix);

    public static string GroupId(string suffix) => SyntheticWeChatNaming.GroupId(suffix);

    public void WriteConfiguration(string yaml) => File.WriteAllText(ConfigurationPath, yaml);

    /// <summary>
    /// Writes the documented <c>collections.yaml</c> shape with one Collection. Membership entries
    /// are written verbatim, so a test can declare duplicates and invalid entries.
    /// </summary>
    public void WriteCollection(string name, params string[] conversationIds)
    {
        var builder = new StringBuilder();
        builder.AppendLine("schema_version: 1.0");
        builder.AppendLine("collections:");
        builder.AppendLine($"  {name}:");
        if (conversationIds.Length == 0)
        {
            builder.AppendLine("    conversations: []");
        }
        else
        {
            builder.AppendLine("    conversations:");
            foreach (var conversationId in conversationIds)
            {
                builder.AppendLine($"      - {conversationId}");
            }
        }

        WriteConfiguration(builder.ToString());
    }

    public Task<IReadOnlyList<ArchiveConversation>> ConversationsAsync() =>
        Archive.ListConversationsAsync(AccountId, CancellationToken.None);

    public async Task<ArchiveConversation?> FindConversationAsync(string sourceConversationId) =>
        (await ConversationsAsync().ConfigureAwait(false))
            .FirstOrDefault(conversation =>
                string.Equals(conversation.SourceConversationId, sourceConversationId, StringComparison.Ordinal));

    public Task<IngestCheckpoint?> ConversationCheckpointAsync(string stableConversationId) =>
        Archive.GetIngestCheckpointAsync(
            AccountId, WeChatCaptureAdapter.Family, "conversation", stableConversationId, CancellationToken.None);

    public Task<IngestCheckpoint?> ConversationCoverageCheckpointAsync(string stableConversationId) =>
        Archive.GetIngestCheckpointAsync(
            AccountId, WeChatCaptureAdapter.Family, "conversation_coverage", stableConversationId, CancellationToken.None);

    public Task<IReadOnlyList<CanonicalMessage>> MessagesAsync(string stableConversationId) =>
        Archive.ReadMessagesAsync(stableConversationId, CancellationToken.None);

    /// <summary>The published Raw Vault generations for this account, oldest first.</summary>
    public Task<IReadOnlyList<RawGenerationSummary>> GenerationsAsync() =>
        Vault.ListGenerationsAsync(AccountId, CancellationToken.None);

    public void Dispose() => Provider.Dispose();
}
