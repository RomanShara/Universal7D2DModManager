using Microsoft.Extensions.DependencyInjection;
using U7D2D.Core.Analysis;
using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Mods;
using U7D2D.Core.Nexus;

namespace U7D2D.Core;

public static class AppServices
{
    /// <summary>Registers every core service. Used by the WPF host and by tests.</summary>
    public static IServiceCollection Register(IServiceCollection services)
    {
        services.AddSingleton<AppPaths>();
        services.AddSingleton(sp =>
        {
            var database = new Database(sp.GetRequiredService<AppPaths>());
            database.Initialize();
            return database;
        });

        services.AddSingleton<SettingsStore>();
        services.AddSingleton<CatalogStore>();
        services.AddSingleton<InstalledStore>();
        services.AddSingleton<BackupStore>();
        services.AddSingleton<LogStore>();
        services.AddSingleton<NexusStore>();

        services.AddSingleton<ArchiveScanner>();
        services.AddSingleton<InstalledModScanner>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<ModInstaller>();

        services.AddSingleton<GameProbe>();
        services.AddSingleton<CompatibilityAnalyzer>();
        services.AddSingleton<ConflictAnalyzer>();
        services.AddSingleton<ModConfigService>();

        // Explicit factory: the second constructor (HttpMessageHandler) is a test seam
        // and must not be picked by the DI container.
        services.AddSingleton(_ => new NexusApiClient());
        services.AddSingleton<NexusUpdateService>();

        services.AddSingleton<ModManagerService>();
        return services;
    }
}
