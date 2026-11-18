using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RavenDb;

namespace RavenDbTests;

// GH-5556: a compensating saga's status and log as a RavenDB document
[Collection("raven")]
public class saga_compensation_5556 : SagaCompensationCompliance
{
    private DatabaseFixture? _driver;
    private IDocumentStore? _store;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _driver = new DatabaseFixture();
        _store = _driver.StartRavenStore();

        opts.Services.AddSingleton(_store);
        opts.UseRavenDbPersistence();
    }

    protected override ValueTask CleanUpAsync()
    {
        _store?.Dispose();
        _driver?.Dispose();
        return ValueTask.CompletedTask;
    }
}
