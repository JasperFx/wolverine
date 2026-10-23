using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

[Collection("raven")]
public class deadletter_admin_compliance : DeadLetterAdminCompliance
{
    private readonly DatabaseFixture _fixture;

    public deadletter_admin_compliance(DatabaseFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        var store = _fixture.StartRavenStore();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton<IDocumentStore>(store);
            })
            .ConfigureServices(services =>
            {
                // RavenDb replays a dead letter the moment it is marked replayable through a data
                // subscription. These tests assert on the replayable flag itself, so take the replayer
                // out of the picture rather than race it
                var replayer = services.Where(x => x.ImplementationType == typeof(DeadLetterQueueReplayer)).ToArray();
                foreach (var descriptor in replayer) services.Remove(descriptor);
            })
            .StartAsync();
    }
}
