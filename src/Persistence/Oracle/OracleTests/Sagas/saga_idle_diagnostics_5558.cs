using IntegrationTests;
using JasperFx.Documents;
using Shouldly;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Oracle;
using Wolverine.RDBMS;

namespace OracleTests.Sagas;

// GH-5558: idle saga diagnostics over the lightweight Oracle saga tables
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.AddSagaType<IdleDiagnosticsSaga>();
        opts.Discovery.IncludeType<OracleGuidIdleSaga>();
        opts.AddSagaType<OracleGuidIdleSaga>();
        opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
    }

    // Oracle keeps a Guid saga id as RAW(16), so the diagnostics must bind and read it as bytes. The compliance
    // saga has a string id (for RavenDB), so this uses a Guid-identified saga of its own
    [Fact]
    public async Task read_and_list_a_saga_with_a_guid_identity()
    {
        var id = Guid.NewGuid();
        await Host.InvokeMessageAndWaitAsync(new StartOracleGuidIdleSaga(id, "oracle"));

        var storage = Host.GetRuntime().SagaStorage;
        var name = typeof(OracleGuidIdleSaga).FullName!;
        var token = TestContext.Current.CancellationToken;

        var state = await storage.ReadSagaAsync(name, id, token);
        state.ShouldNotBeNull();
        state.State.ToString().ShouldContain("oracle");

        var idle = (ISagaIdleDiagnostics)storage;
        var page = await idle.QuerySagasByLastModifiedAsync(name, SagaLastModifiedRange.Any,
            new DocumentQueryOptions(1, 1000), token);
        page.Instances.Select(x => x.Identity).ShouldContain(id);
    }
}

public record StartOracleGuidIdleSaga(Guid Id, string Name);

public class OracleGuidIdleSaga : Saga
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public static OracleGuidIdleSaga Start(StartOracleGuidIdleSaga command)
        => new() { Id = command.Id, Name = command.Name };
}
