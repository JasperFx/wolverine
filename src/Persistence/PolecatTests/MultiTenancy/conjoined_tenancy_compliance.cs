using IntegrationTests;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Exceptions;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Polecat;

namespace PolecatTests.MultiTenancy;

/// <summary>
///     GH-4634. The shared conjoined battery, run against Polecat for the first time. Before this, the
///     whole of <c>src/Persistence/PolecatTests</c> had exactly one tenancy fixture and it was
///     database-per-tenant.
/// </summary>
/// <remarks>
///     Document tenancy is store-wide on Polecat and derived from the event store setting, so there is no
///     <c>AllDocumentsAreMultiTenanted()</c> and no per-type <c>MultiTenanted()</c> to call:
///     <c>Events.TenancyStyle = Conjoined</c> is the whole of it.
/// </remarks>
public class conjoined_tenancy_compliance : ConjoinedTenancyCompliance
{
    protected override void configureStore(WolverineOptions opts, bool defaultTenantUsageEnabled)
    {
        opts.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;

            // Conjoined puts tenant_id into every primary key, so these tables cannot share a schema
            // with a non-tenanted fixture's.
            m.DatabaseSchemaName = "conjoined_battery";

            m.Events.TenancyStyle = TenancyStyle.Conjoined;

            m.DefaultTenantUsageEnabled = defaultTenantUsageEnabled;
        }).IntegrateWithWolverine();
    }

    protected override async Task initializeStoreAsync(IHost host)
    {
        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    protected override Type defaultTenantUsageDisabledExceptionType =>
        typeof(DefaultTenantUsageDisabledException);

    /// <summary>
    ///     Polecat has no <c>TenantIdStyle</c> member at all (jasperfx#876). Its conjoined
    ///     <c>tenant_id</c> comparisons are exact, so Wolverine normalising <c>RED</c> to <c>red</c> on
    ///     <c>MessageContext.TenantId</c> never reaches the value Polecat stores: the session is built
    ///     from <c>Envelope.TenantId</c>, which is never normalised, and the row lands under <c>RED</c>.
    ///     Verified red before being skipped: the stored <c>tenant_id</c> came back <c>'RED'</c>. The
    ///     document is still readable under <c>red</c>, but only because SQL Server's default collation is
    ///     case-insensitive, which is what hides the misspelling until something reads the column.
    /// </summary>
    protected override string? tenantIdStyleSkipReason =>
        "Polecat has no TenantIdStyle of its own (jasperfx#876) and Wolverine never normalises " +
        "Envelope.TenantId, which is what Polecat's OutboxedSessionFactory builds the session from. " +
        "See GH-4640.";

    protected override async Task<TenantedTodo?> LoadTodoAsync(IHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();

        // Polecat has no string-tenant session overloads; SessionOptions is the only way in.
        await using var session = store.QuerySession(new SessionOptions { TenantId = tenantId });
        return await session.LoadAsync<TenantedTodo>(id, TestContext.Current.CancellationToken);
    }

    protected override async Task StoreTodoAsync(IHost host, string tenantId, TenantedTodo todo)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(new SessionOptions { TenantId = tenantId });
        session.Store(todo);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    protected override async Task<string?> StoredTenantIdAsync(IHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(new SessionOptions { TenantId = tenantId });
        var metadata = await session.MetadataForAsync<TenantedTodo>(id, TestContext.Current.CancellationToken);
        return metadata?.TenantId;
    }

    protected override async Task<TenantTally?> LoadTallyAsync(IHost host, string tenantId, Guid id)
    {
        var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(new SessionOptions { TenantId = tenantId });
        return await session.Events.AggregateStreamAsync<TenantTally>(id,
            token: TestContext.Current.CancellationToken);
    }
}
