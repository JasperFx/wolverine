using Alba;
using Fisher;
using JasperFx;
using JasperFx.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Http;
using Wolverine.Http.Runtime.MultiTenancy;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace FisherTests.Http;

/// <summary>
///     GH-4634, item 5. The Fisher mirror of
///     <c>PolecatTests/Http/conjoined_tenancy_http</c>: <c>Wolverine.Http.Fisher</c> had no tenancy test
///     of any kind, so HTTP tenant detection in front of a conjoined Fisher store was entirely unpinned.
/// </summary>
/// <remarks>
///     One SQLite file per host, as everywhere else in this project. Item 6's HTTP half has no Fisher
///     counterpart and says so out loud below rather than silently missing.
/// </remarks>
public class conjoined_tenancy_http : IAsyncLifetime
{
    private readonly List<FisherTestDatabase> _databases = new();
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await startHostAsync(false);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();

        foreach (var database in _databases)
        {
            database.Dispose();
        }
    }

    private async Task<IAlbaHost> startHostAsync(bool assertTenantExists)
    {
        var database = Servers.CreateDatabase("conjoined_http");
        _databases.Add(database);

        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery();

            // Wolverine caches the detected application assembly process-wide, so a host built after
            // another fixture in this assembly inherits THAT application assembly and never sees the
            // endpoints below -- every request 404s. See the deduplication mirror for the same note.
            opts.Discovery.IncludeAssembly(typeof(conjoined_tenancy_http).Assembly);
        });

        builder.Services.AddFisher(m =>
            {
                m.Connection(database.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;

                // A schema decision on Fisher, so it has to be in place before the tables are created.
                m.Policies.AllDocumentsAreMultiTenanted();
                m.Events.TenancyStyle = TenancyStyle.Conjoined;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsRequestHeaderValue("tenant");

            if (assertTenantExists)
            {
                opts.TenantId.AssertExists();
            }

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a conjoined tenancy test endpoint",
                    type => type != typeof(FisherTenancyEndpoint)));
        }));
    }

    [Fact]
    public async Task a_storage_action_writes_into_the_header_tenant()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new FisherTenantedNote(id, "Andor")).ToUrl("/fisher-tenancy/note");
            x.WithRequestHeader("tenant", "one");
            x.StatusCodeShouldBe(204);
        });

        await theHost.Scenario(x =>
        {
            x.Get.Url($"/fisher-tenancy/note/{id}");
            x.WithRequestHeader("tenant", "one");
            x.ContentShouldBe("Andor");
        });

        // The same id under another tenant is a different document, and [Entity(Required = true)] on the
        // GET turns "missing" into a 404 rather than a null body.
        await theHost.Scenario(x =>
        {
            x.Get.Url($"/fisher-tenancy/note/{id}");
            x.WithRequestHeader("tenant", "two");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_write_model_append_stays_inside_the_header_tenant()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new BumpFisherCounter(id, 5)).ToUrl("/fisher-tenancy/counter");
            x.WithRequestHeader("tenant", "one");
            x.StatusCodeShouldBe(204);
        });

        (await countAsync("one", id)).ShouldBe(5);
        (await countAsync("two", id)).ShouldBe(-1);
    }

    [Fact]
    public async Task a_missing_tenant_id_is_a_400_when_the_endpoint_asserts_it_exists()
    {
        var host = await startHostAsync(true);
        try
        {
            var result = await host.Scenario(x =>
            {
                x.Get.Url($"/fisher-tenancy/note/{Guid.NewGuid()}");
                x.StatusCodeShouldBe(400);
            });

            var details = await result.ReadAsJsonAsync<ProblemDetails>();
            details!.Detail.ShouldBe(TenantIdDetection.NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public void the_disabled_default_tenant_case_has_no_fisher_counterpart()
    {
        Assert.Skip(
            "GH-4634 item 6: Fisher has no DefaultTenantUsageEnabled switch and no " +
            "DefaultTenantUsageDisabledException, so there is no way to make a tenant-less request fail " +
            "at the store. See PolecatTests.Http.conjoined_tenancy_http for the case this mirrors.");
    }

    private async Task<int> countAsync(string tenantId, Guid id)
    {
        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantId);
        var counter = await session.Events.AggregateStreamAsync<FisherTenantedCounter>(id,
            token: TestContext.Current.CancellationToken);

        return counter?.Total ?? -1;
    }
}

public record FisherTenantedNote(Guid Id, string Text);

public record BumpFisherCounter(Guid FisherTenantedCounterId, int Amount);

public record FisherCounterBumped(int Amount);

public class FisherTenantedNoteDocument
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class FisherTenantedCounter
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public void Apply(FisherCounterBumped e) => Total += e.Amount;
}

public static class FisherTenancyEndpoint
{
    [WolverinePost("/fisher-tenancy/note")]
    public static IStorageAction<FisherTenantedNoteDocument> Post(FisherTenantedNote request)
        => Storage.Insert(new FisherTenantedNoteDocument { Id = request.Id, Text = request.Text });

    [WolverineGet("/fisher-tenancy/note/{id}")]
    public static string Get(Guid id, [Entity] FisherTenantedNoteDocument note) => note.Text;

    [WolverinePost("/fisher-tenancy/counter")]
    public static EventsToAppend Bump(BumpFisherCounter request,
        [WriteModel(Required = false)] FisherTenantedCounter? counter)
        => new() { new FisherCounterBumped(request.Amount) };
}
