using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RavenDb;

namespace RavenDbTests;

// GH-5839: completing a saga from outside its handlers, as RavenDB documents
[Collection("raven")]
public class saga_termination_5839 : SagaTerminationCompliance
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
