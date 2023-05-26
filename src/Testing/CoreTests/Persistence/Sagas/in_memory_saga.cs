using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using TestingSupport.Sagas;
using Wolverine.Persistence.Sagas;
using Wolverine.Transports;

namespace CoreTests.Persistence.Sagas;

public class InMemorySagaHost : ISagaHost
{
    private IHost _host;

    public IHost BuildHost<TSaga>()
    {
        _host = WolverineHost.For(opts =>
        {
            opts.DisableConventionalDiscovery().IncludeType<TSaga>();

            opts.PublishAllMessages().To(TransportConstants.LocalUri);
        });

        return _host;
    }

    public Task<T> LoadState<T>(Guid id) where T : class
    {
        var loadState = _host.Services.GetRequiredService<InMemorySagaPersistor>().Load<T>(id);
        return Task.FromResult(loadState);
    }

    public Task<T> LoadState<T>(int id) where T : class
    {
        var loadState = _host.Services.GetRequiredService<InMemorySagaPersistor>().Load<T>(id);
        return Task.FromResult(loadState);
    }

    public Task<T> LoadState<T>(long id) where T : class
    {
        var loadState = _host.Services.GetRequiredService<InMemorySagaPersistor>().Load<T>(id);
        return Task.FromResult(loadState);
    }

    public Task<T> LoadState<T>(string id) where T : class
    {
        var loadState = _host.Services.GetRequiredService<InMemorySagaPersistor>().Load<T>(id);
        return Task.FromResult(loadState);
    }
}

public class basic_mechanics_with_guid : GuidIdentifiedSagaComplianceSpecs<InMemorySagaHost>
{
}

public class basic_mechanics_with_int : IntIdentifiedSagaComplianceSpecs<InMemorySagaHost>
{
}

public class basic_mechanics_with_long : LongIdentifiedSagaComplianceSpecs<InMemorySagaHost>
{
}

public class basic_mechanics_with_string : StringIdentifiedSagaComplianceSpecs<InMemorySagaHost>
{
}