using IntegrationTests;
using Shouldly;
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
        opts.PersistMessagesWithOracle(Servers.OracleConnectionString, "WOLVERINE");
    }

    // Oracle keeps a Guid saga id as RAW(16), so the diagnostics must bind and read it as bytes
    [Fact]
    public async Task read_one_saga_by_its_guid_identity()
    {
        var id = Guid.NewGuid();
        await Host.InvokeMessageAndWaitAsync(new StartIdleDiagnosticsSaga(id, "oracle"));

        var state = await Host.GetRuntime().SagaStorage.ReadSagaAsync(typeof(IdleDiagnosticsSaga).FullName!, id,
            TestContext.Current.CancellationToken);

        state.ShouldNotBeNull();
        state.Identity.ToString().ShouldBe(id.ToString());
        state.State.ToString().ShouldContain("oracle");
    }
}
