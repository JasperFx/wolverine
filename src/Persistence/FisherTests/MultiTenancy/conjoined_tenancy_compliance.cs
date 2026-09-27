using Fisher;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Fisher;

namespace FisherTests.MultiTenancy;

/// <summary>
///     GH-4634. The shared conjoined battery, run against Fisher for the first time. Before this,
///     <c>src/Persistence/FisherTests</c> had no <c>MultiTenancy</c> folder at all.
/// </summary>
/// <remarks>
///     <para>
///         Fisher's tenancy surface is Marten-shaped rather than Polecat-shaped: conjoined documents come
///         from <c>Policies.AllDocumentsAreMultiTenanted()</c> and conjoined events from
///         <c>Events.TenancyStyle</c>, set independently.
///     </para>
///     <para>
///         One SQLite file per fixture, as every other Fisher fixture does — the store is a file and not a
///         server, and two concurrently-running test classes on one file is the one-writer failure Fisher's
///         own docs describe as presenting like a hang.
///     </para>
/// </remarks>
public class conjoined_tenancy_compliance : ConjoinedTenancyCompliance
{
    private readonly List<FisherTestDatabase> _databases = new();

    protected override void configureStore(WolverineOptions opts, bool defaultTenantUsageEnabled)
    {
        // A fresh file per host, so the second host the default-tenant test would build cannot collide
        // with this one on the same SQLite file.
        var database = FisherTests.Servers.CreateDatabase("conjoined_battery");
        _databases.Add(database);

        opts.Services.AddFisher(m =>
            {
                m.Connection(database.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;

                // A schema decision on Fisher, so it has to be in place before the tables are created.
                m.Policies.AllDocumentsAreMultiTenanted();
                m.Events.TenancyStyle = TenancyStyle.Conjoined;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();
    }

    protected override Task disposeStoreAsync()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Fisher has no <c>DefaultTenantUsageEnabled</c> switch — neither on <c>StoreOptions</c> nor
    ///     anywhere else in the assembly — and so no <c>DefaultTenantUsageDisabledException</c> to throw.
    /// </summary>
    protected override string? defaultTenantDisabledSkipReason =>
        "Fisher has no DefaultTenantUsageEnabled switch and no DefaultTenantUsageDisabledException, " +
        "so there is nothing to disable. Marten carries it on StoreOptions.Advanced and Polecat " +
        "flattens it onto StoreOptions (polecat#514); Fisher has neither.";

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
        var metadata = await session.MetadataForAsync<TenantedTodo>(id, TestContext.Current.CancellationToken);
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
