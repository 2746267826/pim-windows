using Microsoft.Extensions.DependencyInjection;
using Pim.Client.App.Services;
using Pim.Client.Core.Services.LocalRuntime;
using Xunit;

namespace Pim.Client.App.Tests;

public sealed class LocalAsrCompositionTests
{
    [Fact]
    public async Task Composition_RegistersTypedTransportAndVerifierWithoutSelectingAModel()
    {
        var services = Startup.ConfigureServices();
        await using var asyncDisposable = services as IAsyncDisposable;
        Assert.NotNull(asyncDisposable);

        Assert.IsType<LocalAsrApiTransport>(
            services.GetRequiredService<ILocalAsrNodeTransport>());
        Assert.NotNull(services.GetRequiredService<InstalledLocalAsrArtifactVerifier>());
        Assert.Null(services.GetService<ILocalAsrWorkerRunner>());
        Assert.False(services.GetRequiredService<LocalAsrBacklogViewModel>().IsWorkerAvailable);
    }
}
