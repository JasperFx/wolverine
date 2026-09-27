using IntegrationTests;
using JasperFx.MultiTenancy;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;

namespace MartenTests.MultiTenancy;

/// <summary>
///     GH-4634. The shared conjoined battery, run against Marten. This is the fixture whose behaviour the
///     Polecat and Fisher twins are measured against, because Marten is the only one of the three with a
///     <c>TenantIdStyle</c> of its own.
/// </summary>
public class conjoined_tenancy_compliance : ConjoinedTenancyCompliance
{
    protected override void configureStore(WolverineOptions opts, bool defaultTenantUsageEnabled)
    {
        opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);

                // A dedicated schema: AllDocumentsAreMultiTenanted is a schema decision, so these tables
                // cannot share a schema with any non-tenanted fixture's.
                m.DatabaseSchemaName = "conjoined_battery";
                m.DisableNpgsqlLogging = true;

                // Match the host. A Wolverine host on ForceLowerCase in front of a store left on
                // CaseSensitive is the mismatch the JasperFx docs warn about.
                m.TenantIdStyle = TenantIdStyle.ForceLowerCase;

                m.Policies.AllDocumentsAreMultiTenanted();
                m.Events.TenancyStyle = TenancyStyle.Conjoined;

                m.Advanced.DefaultTenantUsageEnabled = defaultTenantUsageEnabled;
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions();
    }

    protected override Type defaultTenantUsageDisabledExceptionType =>
        typeof(DefaultTenantUsageDisabledException);

    protected override async Task<TenantedTodo?> LoadTodoAsync(IHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantId);
        return await session.LoadAsync<TenantedTodo>(id, TestContext.Current.CancellationToken);
    }

    protected override async Task StoreTodoAsync(IHost host, string tenantId, TenantedTodo todo)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantId);
        session.Store(todo);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    protected override async Task<string?> StoredTenantIdAsync(IHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantId);

        // Marten's MetadataForAsync takes the entity, not the identity.
        var todo = await session.LoadAsync<TenantedTodo>(id, TestContext.Current.CancellationToken);
        if (todo == null) return null;

        var metadata = await session.MetadataForAsync(todo, TestContext.Current.CancellationToken);
        return metadata?.TenantId;
    }

    protected override async Task<TenantTally?> LoadTallyAsync(IHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantId);
        return await session.Events.AggregateStreamAsync<TenantTally>(id,
            token: TestContext.Current.CancellationToken);
    }
}
