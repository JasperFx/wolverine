using IntegrationTests;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Polecat;

namespace PolecatTests.Sagas;

public class PolecatSagaHost : ISagaHost
{
    private IHost _host;

    public IHost BuildHost<TSaga>()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.DisableConventionalDiscovery().IncludeType<TSaga>();

            opts.Services.AddPolecat(x =>
            {
                x.ConnectionString = Servers.SqlServerConnectionString;
                x.DatabaseSchemaName = "sagas";
            }).IntegrateWithWolverine();

            opts.PublishAllMessages().Locally();
        });

        return _host;
    }

    public Task<T> LoadState<T>(Guid id) where T : Wolverine.Saga
    {
        return _host.Services.GetRequiredService<IDocumentStore>().QuerySession().LoadAsync<T>(id);
    }

    public Task<T> LoadState<T>(int id) where T : Wolverine.Saga
    {
        return _host.Services.GetRequiredService<IDocumentStore>().QuerySession().LoadAsync<T>(id);
    }

    public Task<T> LoadState<T>(long id) where T : Wolverine.Saga
    {
        return _host.Services.GetRequiredService<IDocumentStore>().QuerySession().LoadAsync<T>(id);
    }

    public Task<T> LoadState<T>(string id) where T : Wolverine.Saga
    {
        return _host.Services.GetRequiredService<IDocumentStore>().QuerySession().LoadAsync<T>(id);
    }
}
