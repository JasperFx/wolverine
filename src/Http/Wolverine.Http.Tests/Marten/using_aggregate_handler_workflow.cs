using Marten;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Marten;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class using_aggregate_handler_workflow(AppFixture fixture) : IntegrationContext(fixture)
{
    [Theory]
    [InlineData("/orders/create")]
    [InlineData("/orders/create2")]
    public async Task use_marten_command_workflow(string createEndpoint)
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl(createEndpoint);
        });

        var status1 = result1.ReadAsJson<OrderStatus>();
        status1.ShouldNotBeNull();

        await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(status1.OrderId, "Socks", 1)).ToUrl("/orders/itemready");
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Items["Socks"].Ready.ShouldBeTrue();
    }

    [Fact]
    public async Task mix_creation_response_and_start_stream()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create3");
            x.StatusCodeShouldBe(201);
        });

        var response = result1.ReadAsJson<CreationResponse>();
        response.ShouldNotBeNull();
        var raw = response.Url.Split('/').Last();
        var id = Guid.Parse(raw);

        await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(id, "Socks", 1)).ToUrl("/orders/itemready");
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(id);

        order.ShouldNotBeNull();
        order.Items["Socks"].Ready.ShouldBeTrue();
    }

    [Fact]
    public async Task use_a_return_value_as_event()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();
        status1.ShouldNotBeNull();

        await Scenario(x =>
        {
            x.Post.Json(new ShipOrder(status1.OrderId)).ToUrl("/orders/ship");
            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_and_command_aggregate()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();
        status1.ShouldNotBeNull();

        await Scenario(x =>
        {
            x.Post.Json(new ShipOrder2("Something")).ToUrl($"/orders/{status1.OrderId}/ship2");

            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_and_aggregate_but_no_command()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();
        status1.ShouldNotBeNull();

        await Scenario(x =>
        {
            x.Post.Url($"/orders/{status1.OrderId}/ship3");

            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_and_aggregate_but_no_command_expect_404()
    {
        await Scenario(x =>
        {
            x.Post.Url($"/orders/{Guid.NewGuid()}/ship3");

            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_but_no_parameter_and_aggregate_but_no_command()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();
        status1.ShouldNotBeNull();

        await Scenario(x =>
        {
            x.Post.Url($"/orders/{status1.OrderId}/ship4");

            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }
    
    [Fact]
    public async Task use_aggregate_in_endpoint_from_query_param_in_url()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();
        status1.ShouldNotBeNull();

        await Scenario(x =>
        {
            x.Post.Url($"/orders/ship/from-query?id={status1.OrderId}");

            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();

        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task use_stream_collision_policy()
    {
        var id = Guid.NewGuid();

        // First time should be fine
        await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(id, ["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create4");
        });

        // Second time hits an exception from stream id collision
        var result2 = await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(id, ["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create4");
            x.StatusCodeShouldBe(400);
        });

        // And let's verify that we got what we expected for the ProblemDetails
        // in the HTTP response body of the 2nd request
        var details = result2.ReadAsJson<ProblemDetails>();
        details.ShouldNotBeNull();
        var detailsId = details.Extensions["Id"]?.ToString();
        detailsId.ShouldNotBeEmpty();

        Guid.Parse(detailsId).ShouldBe(id);
        details.Detail.ShouldBe($"Duplicated id '{id}'");
    }

    [Fact]
    public async Task accept_response_returns_proper_status_and_url()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status = result.ReadAsJson<OrderStatus>();
        status.ShouldNotBeNull();

        result = await Scenario(x =>
        {
            x.Post.Json(new ConfirmOrder(status.OrderId)).ToUrl($"/orders/{status.OrderId}/confirm");

            x.StatusCodeShouldBe(202);
        });

        var acceptResponse = await result.ReadAsJsonAsync<AcceptResponse>();
        acceptResponse.ShouldNotBeNull();
        acceptResponse.Url.ShouldBe($"/orders/{status.OrderId}");
    }
    
    [Fact]
    public async Task return_updated_aggregate()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status = result.ReadAsJson<OrderStatus>();
        status.ShouldNotBeNull();

        result = await Scenario(x =>
        {
            x.Post.Json(new ConfirmOrder(status.OrderId)).ToUrl($"/orders/{status.OrderId}/confirm2");

            x.StatusCodeShouldBe(200);
        });

        var order = await result.ReadAsJsonAsync<Order>();
        order.IsConfirmed.ShouldBeTrue();
        
    }
    
    [Fact]
    public async Task return_updated_aggregate_in_tuple()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status = result.ReadAsJson<OrderStatus>();
        status.ShouldNotBeNull();

        result = await Scenario(x =>
        {
            x.Post.Json(new ConfirmOrder(status.OrderId)).ToUrl($"/orders/{status.OrderId}/confirm3");

            x.StatusCodeShouldBe(200);
        });

        var order = await result.ReadAsJsonAsync<Order>();
        order.IsConfirmed.ShouldBeTrue();

        using var session = Host.DocumentStore().LightweightSession();
        var stream = await session.Events.FetchStreamAsync(status.OrderId);
        stream.Select(x => x.Data).OfType<UpdatedAggregate>().Any().ShouldBeFalse();

    }

}