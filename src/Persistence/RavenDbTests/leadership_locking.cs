using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Embedded;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;

namespace RavenDbTests;

[Collection("raven")]
public class leadership_locking : IAsyncLifetime
{
    private IDocumentStore _store;
    private IHost _host;

    public leadership_locking(DatabaseFixture fixture)
    {
    }

    public async Task InitializeAsync()
    {
        _store = await EmbeddedServer.Instance.GetDocumentStoreAsync(Guid.NewGuid().ToString());
        _host = await buildHost();
    }

    private async Task<IHost> buildHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "locking";
                opts.UseRavenDbPersistence();

            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task try_to_lock_happy_path()
    {
        var store = _host.Services.GetService<IMessageStore>();
        store.Nodes.HasLeadershipLock().ShouldBeFalse();
        
        var gotLock = await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None);
        gotLock.ShouldBeTrue();
        
        store.Nodes.HasLeadershipLock().ShouldBeTrue();

        await store.Nodes.ReleaseLeadershipLockAsync();
        
        store.Nodes.HasLeadershipLock().ShouldBeFalse();
    }

    [Fact]
    public async Task lock_is_exclusive()
    {
        using var host2 = await buildHost();
        var store2 = host2.Services.GetService<IMessageStore>();
        
        var store = _host.Services.GetService<IMessageStore>();
        store.Nodes.HasLeadershipLock().ShouldBeFalse();
        
        var gotLock = await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None);
        gotLock.ShouldBeTrue();
        
        store.Nodes.HasLeadershipLock().ShouldBeTrue();
        
        (await store2.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None))
            .ShouldBeFalse();
        store2.Nodes.HasLeadershipLock().ShouldBeFalse();

        await store.Nodes.ReleaseLeadershipLockAsync();
        
        store.Nodes.HasLeadershipLock().ShouldBeFalse();
        
        (await store2.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None))
            .ShouldBeTrue();
        store2.Nodes.HasLeadershipLock().ShouldBeTrue();
    }
}