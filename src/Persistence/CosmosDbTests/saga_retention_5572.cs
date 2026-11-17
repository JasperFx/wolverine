using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

// GH-5572: retained, completed sagas as CosmosDB documents. RetainedSaga is this host's only saga type
[Collection("cosmosdb")]
public class saga_retention_5572 : SagaRetentionCompliance
{
    private readonly AppFixture _fixture;

    public saga_retention_5572(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddSingleton(_fixture.Client);
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
    }
}
