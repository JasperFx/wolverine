using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

/// <summary>
/// GH-3590/GH-3596 -- see <see cref="ExclusiveListenerRecoveryCompliance"/>. The last scenario is what pins
/// the <c>CosmosDbDurabilityAgent</c> skipping any destination whose <c>ListenerScope</c> is not
/// <c>CompetingConsumers</c>, and every scenario exercises the null guard around
/// <c>FindListenerCircuit()</c> that landed alongside it.
/// </summary>
[Collection("cosmosdb")]
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance
{
    private readonly AppFixture _fixture;

    public exclusive_listener_recovery_compliance(AppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UseCosmosDbPersistence(AppFixture.DatabaseName);

        // Every test in the "cosmosdb" collection shares the one emulator client, so it is registered as
        // an instance and not a factory -- the host's container must not dispose it on shutdown
        options.Services.AddSingleton(_fixture.Client);
    }
}
