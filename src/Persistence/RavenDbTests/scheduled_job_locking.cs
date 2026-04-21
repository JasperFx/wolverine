using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

[Collection("raven")]
public class scheduled_job_locking : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public scheduled_job_locking(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _store = _fixture.StartRavenStore();
        _host = await buildHost();
    }

    private async Task<IHost> buildHost()
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "scheduled-locking";
                opts.UseRavenDbPersistence();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task try_to_lock_scheduled_happy_path()
    {
        var store = _host.Services.GetService<IMessageStore>()!.As<RavenDbMessageStore>();

        (await store.TryAttainScheduledJobLockAsync(CancellationToken.None)).ShouldBeTrue();

        await store.ReleaseScheduledJobLockAsync();
    }

    [Fact]
    public async Task scheduled_lock_is_exclusive()
    {
        using var host2 = await buildHost();
        var store = _host.Services.GetService<IMessageStore>()!.As<RavenDbMessageStore>();
        var store2 = host2.Services.GetService<IMessageStore>()!.As<RavenDbMessageStore>();

        (await store.TryAttainScheduledJobLockAsync(CancellationToken.None)).ShouldBeTrue();

        (await store2.TryAttainScheduledJobLockAsync(CancellationToken.None)).ShouldBeFalse();

        await store.ReleaseScheduledJobLockAsync();

        (await store2.TryAttainScheduledJobLockAsync(CancellationToken.None)).ShouldBeTrue();
        await store2.ReleaseScheduledJobLockAsync();
    }

    [Fact]
    public async Task scheduled_lock_can_be_reattained_while_leader_lock_is_held()
    {
        var store = _host.Services.GetService<IMessageStore>()!.As<RavenDbMessageStore>();

        (await store.TryAttainScheduledJobLockAsync(CancellationToken.None)).ShouldBeTrue();
        await store.ReleaseScheduledJobLockAsync();

        (await store.Nodes.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();

        (await store.TryAttainScheduledJobLockAsync(CancellationToken.None)).ShouldBeTrue();

        await store.ReleaseScheduledJobLockAsync();
        await store.Nodes.ReleaseLeadershipLockAsync();
    }
}
