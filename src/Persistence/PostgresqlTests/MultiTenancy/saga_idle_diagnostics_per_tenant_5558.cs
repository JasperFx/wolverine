using IntegrationTests;
using JasperFx.Core;
using JasperFx.Documents;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace PostgresqlTests.MultiTenancy;

// GH-5558: under database-per-tenant message storage, the lightweight saga tables live in each tenant's database, and
// the idle saga diagnostics read the named tenant's
public class saga_idle_diagnostics_per_tenant_5558 : MultiTenancyContext
{
    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Discovery.DisableConventionalDiscovery().IncludeType<IdleDiagnosticsSaga>();
        opts.AddSagaType<IdleDiagnosticsSaga>();

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "saga_idle_mt")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
            });

        opts.Services.AddResourceSetupOnStartup();
    }

    [Fact]
    public async Task counts_and_queries_the_named_tenants_saga_tables()
    {
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);

        var red = Guid.NewGuid();
        await theHost.TrackActivity().ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("red", new StartIdleDiagnosticsSaga(red, "red")));

        var blue1 = Guid.NewGuid();
        var blue2 = Guid.NewGuid();
        await theHost.TrackActivity().ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new StartIdleDiagnosticsSaga(blue1, "blue")));
        await theHost.TrackActivity().ExecuteAndWaitAsync(c => c.InvokeForTenantAsync("blue", new StartIdleDiagnosticsSaga(blue2, "blue")));

        await Task.Delay(300.Milliseconds(), TestContext.Current.CancellationToken);
        var end = DateTimeOffset.UtcNow;

        var idle = theHost.GetRuntime().SagaStorage.ShouldBeAssignableTo<ISagaIdleDiagnostics>()!;
        var name = typeof(IdleDiagnosticsSaga).FullName!;
        var token = TestContext.Current.CancellationToken;

        async Task<long> countSince(string? tenantId) =>
            await idle.CountIdleSagasAsync(name, end, tenantId, token)
            - await idle.CountIdleSagasAsync(name, start, tenantId, token);

        (await countSince("red")).ShouldBe(1);
        (await countSince("blue")).ShouldBe(2);
        (await countSince(null)).ShouldBe(0);

        var blue = await idle.QuerySagasByLastModifiedAsync(name, new SagaLastModifiedRange(start, end),
            new DocumentQueryOptions(1, 10) { TenantId = "blue" }, token);
        blue.Instances.Select(x => Guid.Parse(x.Identity.ToString()!)).ShouldBe([blue1, blue2]);
    }
}
