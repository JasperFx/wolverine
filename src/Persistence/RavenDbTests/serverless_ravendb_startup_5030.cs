using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RavenDb;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace RavenDbTests;

[Collection("raven")]
public class ServerlessRavenDbStartup5030
{
    private readonly DatabaseFixture _fixture;

    public ServerlessRavenDbStartup5030(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task LeavesTheBufferedRavenDbControlEndpointInItsSupportedMode()
    {
        using var store = _fixture.StartRavenStore();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IDocumentStore>(store);
                opts.UseRavenDbPersistence();
                opts.Durability.Mode = DurabilityMode.Serverless;
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var endpoint = host.GetRuntime().Options.Transports
            .SelectMany(transport => transport.Endpoints())
            .Single(endpoint => endpoint.Uri.Scheme == "ravendb");

        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);
    }
}
