using Fisher;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Fisher;

namespace FisherTests.Sagas;

/// <summary>
///     Runs the shared saga compliance specifications against Fisher, the Fisher twin of
///     <c>PolecatSagaHost</c>. Each host gets its own SQLite file (see <see cref="Servers" />).
/// </summary>
public class FisherSagaHost : ISagaHost
{
    private IHost _host = null!;

    public async Task<IHost> BuildHostAsync<TSaga>()
    {
        var database = Servers.CreateDatabase("sagas");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<TSaga>();

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(database.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.PublishAllMessages().Locally();
            }).StartAsync();

        return _host;
    }

    public async Task<T?> LoadState<T>(Guid id) where T : Saga
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<T?> LoadState<T>(int id) where T : Saga
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<T?> LoadState<T>(long id) where T : Saga
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.LoadAsync<T>(id);
    }

    public async Task<T?> LoadState<T>(string id) where T : Saga
    {
        await using var session = _host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return await session.LoadAsync<T>(id);
    }
}

public class basic_mechanics_with_guid : GuidIdentifiedSagaComplianceSpecs<FisherSagaHost>;
