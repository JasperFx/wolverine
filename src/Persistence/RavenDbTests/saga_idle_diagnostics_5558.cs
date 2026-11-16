using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RavenDb;

namespace RavenDbTests;

// GH-5558: idle saga diagnostics over RavenDB saga documents
[Collection("raven")]
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    private DatabaseFixture? _driver;
    private IDocumentStore? _store;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _driver = new DatabaseFixture();
        _store = _driver.StartRavenStore();

        opts.Durability.Mode = DurabilityMode.Solo;
        opts.CodeGeneration.GeneratedCodeOutputPath =
            AppContext.BaseDirectory.ParentDirectory()!.ParentDirectory()!.ParentDirectory()!
                .AppendPath("Internal", "Generated");
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

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
