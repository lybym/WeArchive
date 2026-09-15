using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WeArchive.Core.Abstractions;
using WeArchive.Core.Services;
using WeArchive.Infrastructure.Archive;
using WeArchive.Infrastructure.Export;
using WeArchive.Infrastructure.Fixtures;
using WeArchive.Infrastructure.WeChat;

namespace WeArchive.Infrastructure;

/// <summary>Composition root for the infrastructure layer.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the archive, exporter and application services.
    /// The source adapter is registered separately so the host decides whether it talks
    /// to a real client or to the fixture source.
    /// </summary>
    public static IServiceCollection AddWeArchiveCore(this IServiceCollection services, string archivePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IArchiveStore>(sp =>
            new SqliteArchiveStore(archivePath, sp.GetRequiredService<IClock>()));
        services.TryAddSingleton<IDatasetExporter, JsonlDatasetExporter>();
        services.TryAddSingleton<SourceCatalogService>();
        services.TryAddSingleton<ImportService>();
        services.TryAddSingleton<ArchiveWorkflow>();

        return services;
    }

    /// <summary>Registers the Windows WeChat adapter as the source of this archive.</summary>
    public static IServiceCollection AddWeChatWindowsSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISourceAdapter, WeChatWindowsSourceAdapter>();
        return services;
    }

    /// <summary>Registers the synthetic fixture source, used by tests and by demo mode.</summary>
    public static IServiceCollection AddFixtureSource(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISourceAdapter, FixtureSourceAdapter>();
        return services;
    }
}
