using IntegrationTests;
using JasperFx;
using JasperFx.Documents;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using System.Text.Json;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
/// GH-5297: the document diagnostics go through the conjoined builder, so a read is scoped to the requested
/// tenant by the context's own query filter, and a read of every tenant is refused rather than answered with
/// one tenant's rows.
/// </summary>
[Collection("multi-tenancy")]
public class conjoined_document_store_diagnostics : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStoreDiagnostics theDiagnostics = null!;
    private static readonly string itemType = typeof(ScopedItem).FullName!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ScopedItemHandler>();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_ops");
                opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ScopedItemsDbContext>(
                    (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                    AutoCreate.CreateOrUpdate);
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();

        var builder = _host.Services.GetRequiredService<IDbContextBuilder<ScopedItemsDbContext>>();
        var context = await builder.BuildAsync(CancellationToken.None);
        await context.Items.IgnoreQueryFilters().ExecuteDeleteAsync(CancellationToken.None);

        await create("green", "green one");
        await create("green", "green two");
        await create("blue", "blue one");

        theDiagnostics = _host.Services.GetServices<IDocumentStoreDiagnostics>().Single();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private Task create(string tenantId, string name)
    {
        return _host.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync(tenantId, new CreateScopedItem(Guid.NewGuid(), name)));
    }

    private static string[] names(DocumentQueryResult result)
        => result.Documents.Select(x => JsonDocument.Parse(x.Json).RootElement.GetProperty("Name").GetString()!)
            .Order().ToArray();

    [Fact]
    public async Task a_read_is_scoped_to_the_requested_tenant()
    {
        var green = await theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 10) { TenantId = "green" }, CancellationToken.None);
        names(green).ShouldBe(["green one", "green two"]);
        green.TotalCount.ShouldBe(2);

        var blue = await theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 10) { TenantId = "blue" }, CancellationToken.None);
        names(blue).ShouldBe(["blue one"]);
    }

    [Fact]
    public async Task a_filter_composes_with_the_tenant_scope()
    {
        var result = await theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 10) { TenantId = "blue", Where = "Name.StartsWith(@0)", Arguments = ["green"] },
            CancellationToken.None);

        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task a_load_does_not_cross_tenants()
    {
        var green = await theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 1) { TenantId = "green" }, CancellationToken.None);
        var id = green.Documents.Single().Id;

        (await theDiagnostics.LoadDocumentAsync(itemType, id, "green", CancellationToken.None)).ShouldNotBeNull();
        (await theDiagnostics.LoadDocumentAsync(itemType, id, "blue", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task all_tenants_is_refused_rather_than_bypassing_the_filter()
    {
        var ex = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            theDiagnostics.QueryDocumentsAsync(itemType, new DocumentQueryOptions(1, 10) { AllTenants = true },
                CancellationToken.None));
        ex.Criterion.ShouldBe(nameof(DocumentQueryOptions.AllTenants));
    }

    [Fact]
    public async Task all_tenants_with_a_tenant_id_is_a_contradiction()
    {
        await Should.ThrowAsync<ArgumentException>(() => theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 10) { AllTenants = true, TenantId = "green" }, CancellationToken.None));
    }
}
