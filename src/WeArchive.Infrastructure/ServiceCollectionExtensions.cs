using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Collections;
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
    public static IServiceCollection AddWeArchiveCore(
        this IServiceCollection services,
        string archivePath,
        string rawVaultRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawVaultRoot);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IArchiveStore>(sp =>
            new SqliteArchiveStore(archivePath, sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(new RawVaultStore(rawVaultRoot));
        services.TryAddSingleton<IRawVaultStore>(sp => sp.GetRequiredService<RawVaultStore>());
        services.TryAddSingleton(sp => new RebuildService(
            sp.GetRequiredService<IRawVaultStore>(), archivePath, sp.GetRequiredService<IClock>()));
        services.TryAddSingleton(sp => new RawVaultIngestService(
            sp.GetRequiredService<IRawVaultStore>(), sp.GetRequiredService<IArchiveStore>(), sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IDatasetExporter, JsonlDatasetExporter>();
        services.TryAddSingleton<SourceCatalogService>();
        services.TryAddSingleton<ImportService>();
        services.TryAddSingleton<ArchiveWorkflow>();
        services.TryAddSingleton<CaptureService>();

        return services;
    }

    public static IServiceCollection AddCollectionCatalog(this IServiceCollection services, string collectionsPath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionsPath);
        services.TryAddSingleton<ICollectionCatalogStore>(_ => new YamlCollectionCatalogStore(collectionsPath));
        services.TryAddSingleton<CollectionCatalogService>();
        services.TryAddSingleton(sp => new CollectionSyncService(
            sp.GetRequiredService<CollectionCatalogService>(),
            sp.GetRequiredService<SourceCatalogService>(),
            sp.GetRequiredService<CaptureService>(),
            sp.GetRequiredService<RawVaultIngestService>()));
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
