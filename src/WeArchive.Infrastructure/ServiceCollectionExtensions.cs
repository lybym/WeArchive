using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Collections;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Collections;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.RawVault;
using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Infrastructure;

/// <summary>Composition root for the infrastructure layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the archive, exporter, Raw Vault store and application services.
    /// The source adapter and capture adapter are registered separately so the host decides
    /// whether it talks to a real client or to the fixture source.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="archivePath">Absolute path of the canonical SQLite archive.</param>
    /// <param name="rawVaultRoot">Absolute path of the Raw Vault generation root.</param>
    /// <param name="collectionConfigurationPath">
    /// Absolute path of the authoritative Collection configuration file. When omitted, no
    /// Collection configuration is configured and the catalog resolves as empty
    /// (docs/adr/0009-collection-configuration-ownership.md).
    /// </param>
    public static IServiceCollection AddWeArchiveCore(
        this IServiceCollection services,
        string archivePath,
        string rawVaultRoot,
        string? collectionConfigurationPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawVaultRoot);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IArchiveStore>(sp =>
            new SqliteArchiveStore(archivePath, sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(_ => new RawVaultStore(rawVaultRoot));
        services.TryAddSingleton(new VaultInspectionService(rawVaultRoot));
        services.TryAddSingleton<IRawVaultStore>(sp => sp.GetRequiredService<RawVaultStore>());
        services.TryAddSingleton(sp => new RebuildService(
            sp.GetRequiredService<IRawVaultStore>(), archivePath, sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(sp => new RawVaultIngestService(
            sp.GetRequiredService<IRawVaultStore>(), sp.GetRequiredService<IArchiveStore>(), sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IConversationIngestService>(sp =>
            sp.GetRequiredService<RawVaultIngestService>());
        // Ingest progress is projected by the component that owns the checkpoint encoding, not by
        // the canonical archive store (docs/DATA_MODEL.md section 23.3).
        services.TryAddSingleton<IIngestProgressSource>(sp =>
            sp.GetRequiredService<RawVaultIngestService>());
        services.TryAddSingleton<IDatasetExporter, JsonlDatasetExporter>();
        services.TryAddSingleton<SourceCatalogService>();
        services.TryAddSingleton<ImportService>();
        // Export is a canonical-archive-only derivation: the workflow reads the archive and the
        // exporter and nothing else, and selectors resolve from archived conversations (Issue #66).
        services.TryAddSingleton<ArchiveConversationResolver>();
        services.TryAddSingleton<ArchiveWorkflow>();
        services.TryAddSingleton<ArchiveQueryService>();
        services.TryAddSingleton<CaptureService>();
        // The one preservation-first sync boundary every scope depends on: capture -> Raw Vault ->
        // conversation-scoped incremental ingest (docs/ARCHITECTURE.md sections 3.2 and 3.8).
        services.TryAddSingleton<SyncOrchestrationService>();
        services.TryAddSingleton<ConversationSyncService>();
        services.TryAddSingleton<ICollectionCatalogSource>(
            _ => new YamlCollectionCatalogSource(collectionConfigurationPath));
        services.TryAddSingleton<CollectionCatalogService>();
        services.TryAddSingleton<CollectionSyncService>();

        return services;
    }

    /// <summary>Registers the Windows WeChat adapter and capture adapter as the source of this archive.</summary>
    public static IServiceCollection AddWeChatWindowsSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISourceAdapter, WeChatWindowsSourceAdapter>();
        services.TryAddSingleton<ISourceCaptureAdapter, WeChatCaptureAdapter>();
        return services;
    }

    /// <summary>Registers the synthetic fixture source and fixture capture adapter, used by tests and demo mode.</summary>
    public static IServiceCollection AddFixtureSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISourceAdapter, FixtureSourceAdapter>();
        services.TryAddSingleton<ISourceCaptureAdapter, FixtureCaptureAdapter>();
        return services;
    }
}
