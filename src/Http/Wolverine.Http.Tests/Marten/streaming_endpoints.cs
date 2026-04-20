using Alba;
using Marten;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// Tests for <see cref="StreamOne{T}"/>, <see cref="StreamMany{T}"/>, and
/// <see cref="StreamAggregate{T}"/> from <c>Marten.AspNetCore</c>. Wolverine.Http
/// dispatches these as ordinary <c>IResult</c> return values via the existing
/// <c>ResultWriterPolicy</c> — no Wolverine-specific code required. GH-1562.
/// </summary>
public class streaming_endpoints(AppFixture fixture) : IntegrationContext(fixture)
{
    // ───────────────────────── StreamOne<T> ─────────────────────────

    [Fact]
    public async Task stream_one_returns_matching_document_as_json()
    {
        var invoice = new Invoice { Id = Guid.NewGuid(), Approved = true };
        await using (var session = Store.LightweightSession())
        {
            session.Store(invoice);
            await session.SaveChangesAsync();
        }

        var body = await Host.GetAsJson<Invoice>($"/streaming/invoice/{invoice.Id}");

        body.ShouldNotBeNull();
        body.Id.ShouldBe(invoice.Id);
        body.Approved.ShouldBeTrue();
    }

    [Fact]
    public async Task stream_one_sets_content_type_and_status_on_hit()
    {
        var invoice = new Invoice { Id = Guid.NewGuid() };
        await using (var session = Store.LightweightSession())
        {
            session.Store(invoice);
            await session.SaveChangesAsync();
        }

        var result = await Host.Scenario(x =>
        {
            x.Get.Url($"/streaming/invoice/{invoice.Id}");
            x.StatusCodeShouldBe(200);
            x.ContentTypeShouldBe("application/json");
        });

        // Content-Length should be set (single-document streaming buffers in memory)
        result.Context.Response.ContentLength.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task stream_one_returns_404_when_no_match()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url($"/streaming/invoice/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task stream_one_respects_custom_on_found_status()
    {
        var invoice = new Invoice { Id = Guid.NewGuid() };
        await using (var session = Store.LightweightSession())
        {
            session.Store(invoice);
            await session.SaveChangesAsync();
        }

        await Host.Scenario(x =>
        {
            x.Get.Url($"/streaming/invoice/{invoice.Id}/custom-status");
            x.StatusCodeShouldBe(202);
        });
    }

    [Fact]
    public async Task stream_one_respects_custom_content_type()
    {
        var invoice = new Invoice { Id = Guid.NewGuid() };
        await using (var session = Store.LightweightSession())
        {
            session.Store(invoice);
            await session.SaveChangesAsync();
        }

        await Host.Scenario(x =>
        {
            x.Get.Url($"/streaming/invoice/{invoice.Id}/custom-content-type");
            x.StatusCodeShouldBe(200);
            x.ContentTypeShouldBe("application/vnd.wolverine.invoice+json");
        });
    }

    // ───────────────────────── StreamMany<T> ─────────────────────────

    [Fact]
    public async Task stream_many_returns_json_array()
    {
        // Use a distinct Approved=true batch keyed by this test's invoices so assertions
        // are stable even across concurrent test runs against a shared document store.
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using (var session = Store.LightweightSession())
        {
            foreach (var id in ids) session.Store(new Invoice { Id = id, Approved = true });
            await session.SaveChangesAsync();
        }

        var body = await Host.GetAsJson<List<Invoice>>("/streaming/invoices/approved");

        body.ShouldNotBeNull();
        foreach (var id in ids)
            body.ShouldContain(x => x.Id == id);
    }

    [Fact]
    public async Task stream_many_returns_empty_array_when_no_match_not_404()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/streaming/invoices/none");
            x.StatusCodeShouldBe(200);
            x.ContentTypeShouldBe("application/json");
        });

        var body = result.ReadAsText();
        body.Trim().ShouldBe("[]");
    }

    // ───────────────────── StreamAggregate<T> ─────────────────────

    [Fact]
    public async Task stream_aggregate_returns_latest_aggregate_as_json()
    {
        // Use the existing /orders/create endpoint to set up an event-sourced Order
        var created = await Host.Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes"])).ToUrl("/orders/create");
        });

        var status = created.ReadAsJson<OrderStatus>();

        var body = await Host.GetAsJson<Order>($"/streaming/order/{status.OrderId}");

        body.ShouldNotBeNull();
        body.Id.ShouldBe(status.OrderId);
        body.Items.Keys.OrderBy(x => x).ShouldBe(new[] { "Shoes", "Socks" });
    }

    [Fact]
    public async Task stream_aggregate_returns_404_for_unknown_id()
    {
        await Host.Scenario(x =>
        {
            x.Get.Url($"/streaming/order/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }

    // ───────────────────────── OpenAPI metadata ─────────────────────────

    [Fact]
    public void stream_one_endpoint_advertises_produces_T_and_404_in_metadata()
    {
        var metadata = EndpointMetadataFor("GET", "/streaming/invoice/{id}");

        metadata.OfType<IProducesResponseTypeMetadata>()
            .ShouldContain(m => m.StatusCode == 200 && m.Type == typeof(Invoice));

        metadata.OfType<IProducesResponseTypeMetadata>()
            .ShouldContain(m => m.StatusCode == 404);
    }

    [Fact]
    public void stream_many_endpoint_advertises_produces_array_in_metadata()
    {
        var metadata = EndpointMetadataFor("GET", "/streaming/invoices/approved");

        metadata.OfType<IProducesResponseTypeMetadata>()
            .ShouldContain(m => m.StatusCode == 200 && m.Type == typeof(IReadOnlyList<Invoice>));
    }

    [Fact]
    public void stream_aggregate_endpoint_advertises_produces_T_and_404_in_metadata()
    {
        var metadata = EndpointMetadataFor("GET", "/streaming/order/{id}");

        metadata.OfType<IProducesResponseTypeMetadata>()
            .ShouldContain(m => m.StatusCode == 200 && m.Type == typeof(Order));
        metadata.OfType<IProducesResponseTypeMetadata>()
            .ShouldContain(m => m.StatusCode == 404);
    }

    private EndpointMetadataCollection EndpointMetadataFor(string method, string pattern)
    {
        var dataSource = Host.Services.GetServices<EndpointDataSource>()
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault(x =>
                x.RoutePattern.RawText == pattern &&
                x.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

        dataSource.ShouldNotBeNull($"No endpoint found for {method} {pattern}");
        return dataSource.Metadata;
    }
}
