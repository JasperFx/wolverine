using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Azure.Cosmos;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

public class CosmosDbSagaHost : ISagaHost
{
    private readonly AppFixture _fixture;

    public CosmosDbSagaHost()
    {
        _fixture = new AppFixture();
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
        _fixture.InitializeAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

    }

    public Task<IHost> BuildHostAsync<TSaga>()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.CodeGeneration.GeneratedCodeOutputPath = AppContext.BaseDirectory.ParentDirectory()!
                    .ParentDirectory()!.ParentDirectory()!.AppendPath("Internal", "Generated");
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.Discovery.IncludeType<StringBasicWorkflow>();
                opts.Discovery.IncludeAssembly(typeof(StringBasicWorkflow).Assembly);

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
    }

    public Task<T?> LoadState<T>(Guid id) where T : Saga
    {
        throw new NotSupportedException();
    }

    public Task<T?> LoadState<T>(int id) where T : Saga
    {
        throw new NotSupportedException();
    }

    public Task<T?> LoadState<T>(long id) where T : Saga
    {
        throw new NotSupportedException();
    }

    public async Task<T?> LoadState<T>(string id) where T : Saga
    {
        try
        {
            var response = await _fixture.Container.ReadItemAsync<T>(id, PartitionKey.None);
            return response.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default!;
        }
    }
}

// CosmosDbSagaHost stands up real Wolverine hosts against the one shared emulator database, so this suite
// has to be serialized with the rest of the CosmosDb tests. Left outside the collection it ran in parallel
// and its node/durability churn intermittently released inbox ownership out from under other suites.
[Collection("cosmosdb")]
public class saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<CosmosDbSagaHost>
{
    public saga_storage_compliance()
    {
    }
}
