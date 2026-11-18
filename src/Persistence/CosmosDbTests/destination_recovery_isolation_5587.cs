using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>. The CosmosDB durability agent ran incoming and
/// then outgoing recovery one destination after another in one loop, so a single stuck destination stopped all of it.
/// </summary>
[Collection("cosmosdb")]
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    private readonly AppFixture _fixture;

    public destination_recovery_isolation_5587(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.UseCosmosDbPersistence(AppFixture.DatabaseName);

        // Every test in the "cosmosdb" collection shares the one emulator client
        opts.Services.AddSingleton(_fixture.Client);
    }

    protected override async Task ResetStateAsync(Microsoft.Extensions.Hosting.IHost host)
    {
        await _fixture.ClearAll();
    }
}
