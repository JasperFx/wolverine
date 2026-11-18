using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

// GH-5556: a compensating saga's status and log as a CosmosDB document
[Collection("cosmosdb")]
public class saga_compensation_5556 : SagaCompensationCompliance
{
    private readonly AppFixture _fixture;

    public saga_compensation_5556(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddSingleton(_fixture.Client);
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
    }
}
