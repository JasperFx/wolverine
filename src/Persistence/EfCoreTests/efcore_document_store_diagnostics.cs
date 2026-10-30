using IntegrationTests;
using JasperFx.Documents;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using SharedPersistenceModels.Items;
using System.Text.Json;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Microsoft.Data.SqlClient;
using Weasel.SqlServer;
using Wolverine.SqlServer;

namespace EfCoreTests;

/// <summary>
/// GH-5297: <see cref="IDocumentStoreDiagnostics"/> over a plain (single-database) <see cref="DbContext"/>
/// that has Wolverine's envelope storage mapped into it.
/// </summary>
[Collection("sqlserver")]
public class efcore_document_store_diagnostics : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStoreDiagnostics theDiagnostics = null!;
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();
    private readonly Guid _third = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("mt_items");
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opts.Services.AddDbContextWithWolverineIntegration<SampleMappedDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();

        await using (var scope = _host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SampleMappedDbContext>();
            await db.Items.AddRangeAsync(
                new Item { Id = _first, Name = "alpha", Approved = true },
                new Item { Id = _second, Name = "bravo" },
                new Item { Id = _third, Name = "charlie", Approved = true });
            await db.SaveChangesAsync();
        }

        theDiagnostics = _host.Services.GetServices<IDocumentStoreDiagnostics>().Single();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static string itemType => typeof(Item).FullName!;

    [Fact]
    public void the_subject_is_the_efcore_uri()
    {
        theDiagnostics.Subject.ShouldBe(new Uri("efcore://SampleMappedDbContext"));
    }

    [Fact]
    public async Task lists_entities_and_identifies_the_wolverine_ones()
    {
        var types = await theDiagnostics.DocumentTypesAsync(CancellationToken.None);

        types.ShouldContain(x => x.TypeName == itemType);
        types.Single(x => x.TypeName == itemType).Alias.ShouldBe("items");

        var wolverine = types.Where(WolverineMappedEntities.IsWolverineEntity).Select(x => x.TypeName).ToArray();
        wolverine.ShouldBe([typeof(IncomingMessage).FullName!, typeof(OutgoingMessage).FullName!],
            ignoreOrder: true);
        WolverineMappedEntities.IsWolverineEntity(itemType).ShouldBeFalse();
    }

    [Fact]
    public async Task pages_in_a_stable_order_with_the_total()
    {
        var one = await theDiagnostics.QueryDocumentsAsync(itemType, new DocumentQueryOptions(1, 2),
            CancellationToken.None);
        var two = await theDiagnostics.QueryDocumentsAsync(itemType, new DocumentQueryOptions(2, 2),
            CancellationToken.None);

        one.TotalCount.ShouldBe(3);
        one.Documents.Count.ShouldBe(2);
        two.Documents.Count.ShouldBe(1);
        one.Documents.Concat(two.Documents).Select(x => x.Id)
            .ShouldBe(new[] { _first, _second, _third }.Select(x => x.ToString()), ignoreOrder: true);
    }

    [Fact]
    public async Task serializes_scalar_properties_only()
    {
        var result = await theDiagnostics.QueryDocumentsAsync(itemType, new DocumentQueryOptions(1, 10)
        {
            Where = "Name == @0", Arguments = ["alpha"]
        }, CancellationToken.None);

        var doc = result.Documents.Single();
        using var json = JsonDocument.Parse(doc.Json);
        json.RootElement.GetProperty("Name").GetString().ShouldBe("alpha");
        json.RootElement.GetProperty("Approved").GetBoolean().ShouldBeTrue();
        json.RootElement.TryGetProperty("Events", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task filters_and_orders_by_entity_properties()
    {
        var result = await theDiagnostics.QueryDocumentsAsync(itemType, new DocumentQueryOptions(1, 10)
        {
            Where = "Approved == true", OrderBy = "Name desc"
        }, CancellationToken.None);

        result.TotalCount.ShouldBe(2);
        result.Documents.Select(x => JsonDocument.Parse(x.Json).RootElement.GetProperty("Name").GetString())
            .ShouldBe(["charlie", "alpha"]);
    }

    [Fact]
    public async Task an_unknown_member_is_refused_rather_than_ignored()
    {
        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => theDiagnostics.QueryDocumentsAsync(
            itemType, new DocumentQueryOptions(1, 10) { Where = "NoSuchProperty == 1" }, CancellationToken.None));
    }

    [Fact]
    public async Task unsupported_criteria_are_refused()
    {
        var ex = await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() =>
            theDiagnostics.QueryDocumentsAsync(itemType,
                new DocumentQueryOptions(1, 10) { OrderByMetadata = DocumentMetadataOrder.NewestFirst },
                CancellationToken.None));
        ex.Criterion.ShouldBe(nameof(DocumentQueryOptions.OrderByMetadata));

        await Should.ThrowAsync<DocumentCriteriaNotSupportedException>(() => theDiagnostics.QueryDocumentsAsync(
            itemType, new DocumentQueryOptions(1, 10) { CorrelationId = "x" }, CancellationToken.None));
    }

    [Fact]
    public async Task id_equals_narrows_to_one_row()
    {
        var result = await theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 10, _second.ToString().ToUpperInvariant()), CancellationToken.None);

        result.TotalCount.ShouldBe(1);
        result.Documents.Single().Id.ShouldBe(_second.ToString());

        var none = await theDiagnostics.QueryDocumentsAsync(itemType,
            new DocumentQueryOptions(1, 10, "not-a-guid"), CancellationToken.None);
        none.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task loads_by_primary_key()
    {
        var doc = await theDiagnostics.LoadDocumentAsync(itemType, _third.ToString(), null, CancellationToken.None);
        doc.ShouldNotBeNull();
        JsonDocument.Parse(doc.Json).RootElement.GetProperty("Name").GetString().ShouldBe("charlie");

        (await theDiagnostics.LoadDocumentAsync(itemType, Guid.NewGuid().ToString(), null, CancellationToken.None))
            .ShouldBeNull();
        (await theDiagnostics.LoadDocumentAsync(itemType, "garbage", null, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task unknown_type_reads_as_empty()
    {
        var result = await theDiagnostics.QueryDocumentsAsync("No.Such.Type", new DocumentQueryOptions(1, 10),
            CancellationToken.None);
        result.TotalCount.ShouldBe(0);
        (await theDiagnostics.LoadDocumentAsync("No.Such.Type", "1", null, CancellationToken.None)).ShouldBeNull();
        (await theDiagnostics.ExplainDocumentQueryAsync("No.Such.Type", new DocumentQueryOptions(1, 10),
            CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task explain_returns_the_generated_sql()
    {
        var plan = await theDiagnostics.ExplainDocumentQueryAsync(itemType,
            new DocumentQueryOptions(1, 10) { Where = "Name == @0", Arguments = ["alpha"] }, CancellationToken.None);

        plan.ShouldNotBeNull();
        plan.PlanText.ShouldContain("SELECT");
        plan.PlanText.ShouldContain("items");
    }

    [Fact]
    public async Task wolverine_entities_can_still_be_read()
    {
        var result = await theDiagnostics.QueryDocumentsAsync(typeof(IncomingMessage).FullName!,
            new DocumentQueryOptions(1, 10), CancellationToken.None);
        result.TotalCount.ShouldBeGreaterThanOrEqualTo(0);
    }
}
