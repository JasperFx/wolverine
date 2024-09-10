using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Embedded;
using Raven.TestDriver;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RavenDb;

namespace RavenDbTests;

public class RavenDbSagaHost : RavenTestDriver, ISagaHost
{
    private IDocumentStore _store;
    
    public IHost BuildHost<TSaga>()
    {
        _store = GetDocumentStore();

        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.CodeGeneration.GeneratedCodeOutputPath = AppContext.BaseDirectory.ParentDirectory().ParentDirectory().ParentDirectory().AppendPath("Internal", "Generated");
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                // Shouldn't be necessary, but apparently is. Type scanning is not working
                // for some reason across the compliance tests
                opts.Discovery.IncludeType<StringBasicWorkflow>();
                opts.Discovery.IncludeAssembly(typeof(StringBasicWorkflow).Assembly);
                
                opts.Services.AddSingleton(_store);
                opts.UseRavenDbPersistence();
            }).Start();
    }

    public Task<T> LoadState<T>(Guid id) where T : Saga
    {
        throw new NotSupportedException();
    }

    public Task<T> LoadState<T>(int id) where T : Saga
    {
        throw new NotSupportedException();
    }

    public Task<T> LoadState<T>(long id) where T : Saga
    {
        throw new NotSupportedException();
    }

    public async Task<T> LoadState<T>(string id) where T : Saga
    {
        using var session = _store.OpenAsyncSession();
        return await session.LoadAsync<T>(id);
    }
}

[CollectionDefinition("raven")]
public class saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<RavenDbSagaHost>
{
    public saga_storage_compliance()
    {
    }
}