using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

// GH-5839: completing a saga from outside its handlers, as CosmosDB documents
[Collection("cosmosdb")]
public class saga_termination_5839 : SagaTerminationCompliance
{
    private readonly AppFixture _fixture;

    public saga_termination_5839(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.Services.AddSingleton(_fixture.Client);
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
    }
}
