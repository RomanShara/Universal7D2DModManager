using Microsoft.Extensions.DependencyInjection;
using U7D2D.Core.Nexus;

namespace U7D2D.Core.Tests;

/// <summary>
/// Startup smoke test: the WPF host resolves ModManagerService at launch, so a missing
/// registration in AppServices.Register would crash the app before any window appears.
/// </summary>
public sealed class AppServicesTests
{
    [Fact]
    public void Every_core_service_including_nexus_can_be_resolved()
    {
        var services = new ServiceCollection();
        AppServices.Register(services);

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<ModManagerService>());
        Assert.NotNull(provider.GetRequiredService<NexusApiClient>());
        Assert.NotNull(provider.GetRequiredService<NexusUpdateService>());
    }
}
