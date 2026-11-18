using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.DestinationIsolation;
using Wolverine.RavenDb;

namespace RavenDbTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>. The RavenDB durability agent ran incoming and
/// then outgoing recovery one destination after another in one loop, so a single stuck destination stopped all of it.
/// </summary>
[Collection("raven")]
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance
{
    private readonly IDocumentStore _store;

    public destination_recovery_isolation_5587(DatabaseFixture fixture)
    {
        // A brand new database for every test method
        _store = fixture.StartRavenStore();
    }

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.UseRavenDbPersistence();

        // An instance rather than a factory, so the host does not dispose the fixture's store
        opts.Services.AddSingleton(_store);
    }
}
