using Alba;
using IntegrationTests;
using JasperFx.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Polecat.Exceptions;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.Runtime.MultiTenancy;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Polecat;

namespace PolecatTests.Http;

/// <summary>
///     GH-4634, item 5 and the HTTP half of item 6. <c>Wolverine.Http.Polecat</c> had no tenancy test of
///     any kind: HTTP tenant detection in front of a conjoined Polecat store was entirely unpinned.
/// </summary>
/// <remarks>
///     <para>
///         Mirrored almost line for line by <c>FisherTests/Http/conjoined_tenancy_http.cs</c>. The two live
///         in their own store's test project rather than in a shared one for the same reason the
///         deduplication mirrors do: a Polecat endpoint type in <c>Wolverine.Http.Tests</c> would be
///         scanned by every other host in that assembly, and that project's CI lane has no SQL Server 2025.
///     </para>
///     <para>
///         Tenant detection itself lives in <c>Wolverine.Http</c> and is store-agnostic, so what is being
///         pinned here is the join: does the detected tenant actually reach the Polecat session the
///         endpoint's <c>[Entity]</c>, storage action and <c>[WriteModel]</c> frames open?
///     </para>
/// </remarks>
public class conjoined_tenancy_http : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await startHostAsync(true);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private static async Task<IAlbaHost> startHostAsync(bool defaultTenantUsageEnabled)
    {
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

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "conjoined_http";
            m.Events.TenancyStyle = TenancyStyle.Conjoined;
            m.DefaultTenantUsageEnabled = defaultTenantUsageEnabled;
        }).IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsRequestHeaderValue("tenant");

            // Deliberately NOT AssertExists() on this host: the sad path and the no-assertion path are
            // two different fixtures below, and this one is the happy path.
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a conjoined tenancy test endpoint",
                    type => type != typeof(PolecatTenancyEndpoint)));
        }));

        var store = (DocumentStore)host.Services.GetRequiredService<IDocumentStore>();
        await store.Database.ApplyAllConfiguredChangesToDatabaseAsync();

        return host;
    }

    [Fact]
    public async Task a_storage_action_writes_into_the_header_tenant()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new TenantedNote(id, "Andor")).ToUrl("/polecat-tenancy/note");
            x.WithRequestHeader("tenant", "one");
            x.StatusCodeShouldBe(204);
        });

        await theHost.Scenario(x =>
        {
            x.Get.Url($"/polecat-tenancy/note/{id}");
            x.WithRequestHeader("tenant", "one");
            x.ContentShouldBe("Andor");
        });

        // The same id under another tenant is a different document, and [Entity(Required = true)] on the
        // GET turns "missing" into a 404 rather than a null body.
        await theHost.Scenario(x =>
        {
            x.Get.Url($"/polecat-tenancy/note/{id}");
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
            x.Post.Json(new BumpCounter(id, 5)).ToUrl("/polecat-tenancy/counter");
            x.WithRequestHeader("tenant", "one");
            x.StatusCodeShouldBe(204);
        });

        (await countAsync("one", id)).ShouldBe(5);
        (await countAsync("two", id)).ShouldBe(-1);
    }

    [Fact]
    public async Task a_missing_tenant_id_is_a_400_when_the_endpoint_asserts_it_exists()
    {
        var host = await startAssertingHostAsync();
        try
        {
            var result = await host.Scenario(x =>
            {
                x.Get.Url($"/polecat-tenancy/note/{Guid.NewGuid()}");
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

    /// <summary>
    ///     GH-4634 item 6, the HTTP half, and an open question rather than a settled rule. With the store's
    ///     default tenant disabled and no <c>AssertExists()</c> in front, a request with no tenant header
    ///     binds <c>*DEFAULT*</c>, reaches the endpoint, and the store throws on the session. Nothing maps
    ///     that exception: it escapes the endpoint entirely, which in a deployed application is an
    ///     unhandled-exception 500 for what is really a bad request.
    /// </summary>
    /// <remarks>
    ///     This test pins TODAY's behaviour deliberately, as the escaping exception rather than as a
    ///     status code -- an Alba host has no exception handler middleware, so the throw is what is
    ///     observable here and the 500 is what the same request produces in production. Whether a store's
    ///     <c>DefaultTenantUsageDisabledException</c> should map to the same 400 +
    ///     <c>ProblemDetails</c> that <c>AssertExists()</c> produces is a design decision, not a bug fix --
    ///     the two are not obviously the same thing (one is "you did not send a tenant", the other is "this
    ///     application has no default tenant"), and changing it would change the answer for every existing
    ///     application that disabled the default tenant. Raised on #4634 for a call.
    /// </remarks>
    [Fact]
    public async Task without_assert_exists_a_disabled_default_tenant_escapes_unmapped()
    {
        var host = await startHostAsync(false);
        try
        {
            await Should.ThrowAsync<DefaultTenantUsageDisabledException>(() => host.Scenario(x =>
            {
                x.Post.Json(new TenantedNote(Guid.NewGuid(), "Nowhere")).ToUrl("/polecat-tenancy/note");

                // No status assertion: the point is that the request never produced one.
                x.IgnoreStatusCode();
            }));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static async Task<IAlbaHost> startAssertingHostAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(conjoined_tenancy_http).Assembly);
        });

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = "conjoined_http";
            m.Events.TenancyStyle = TenancyStyle.Conjoined;
        }).IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
        {
            opts.TenantId.IsRequestHeaderValue("tenant");
            opts.TenantId.AssertExists();

            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a conjoined tenancy test endpoint",
                    type => type != typeof(PolecatTenancyEndpoint)));
        }));
    }

    private async Task<int> countAsync(string tenantId, Guid id)
    {
        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(new Polecat.SessionOptions { TenantId = tenantId });
        var counter = await session.Events.AggregateStreamAsync<TenantedCounter>(id,
            token: TestContext.Current.CancellationToken);

        return counter?.Total ?? -1;
    }
}

public record TenantedNote(Guid Id, string Text);

public record BumpCounter(Guid TenantedCounterId, int Amount);

public record CounterBumped(int Amount);

public class TenantedNoteDocument
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class TenantedCounter
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public void Apply(CounterBumped e) => Total += e.Amount;
}

public static class PolecatTenancyEndpoint
{
    [WolverinePost("/polecat-tenancy/note")]
    public static IStorageAction<TenantedNoteDocument> Post(TenantedNote request)
        => Storage.Insert(new TenantedNoteDocument { Id = request.Id, Text = request.Text });

    [WolverineGet("/polecat-tenancy/note/{id}")]
    public static string Get(Guid id, [Entity] TenantedNoteDocument note) => note.Text;

    [WolverinePost("/polecat-tenancy/counter")]
    public static EventsToAppend Bump(BumpCounter request,
        [WriteModel(Required = false)] TenantedCounter? counter)
        => new() { new CounterBumped(request.Amount) };
}
