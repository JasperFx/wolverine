using Microsoft.AspNetCore.Mvc;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class using_aggregate_handler_workflow : IntegrationContext
{
    public using_aggregate_handler_workflow(AppFixture fixture) : base(fixture)
    {
    }

    [Theory]
    [InlineData("/orders/create")]
    [InlineData("/orders/create2")]
    public async Task use_marten_command_workflow(string createEndpoint)
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl(createEndpoint);
        });

        var status1 = result1.ReadAsJson<OrderStatus>();

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
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create3");
            x.StatusCodeShouldBe(201);
        });
        
        var response = result1.ReadAsJson<CreationResponse>();
        var raw = response.Url.ToString().Split('/').Last();
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
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();

        await Scenario(x =>
        {
            x.Post.Json(new ShipOrder(status1.OrderId)).ToUrl("/orders/ship");
            x.StatusCodeShouldBe(204);
        });

        using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);
        order.Shipped.HasValue.ShouldBeTrue();
    }
    
    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_and_command_aggregate()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();

        await Scenario(x =>
        {
            x.Post.Json(new ShipOrder2("Something")).ToUrl($"/orders/{status1.OrderId}/ship2");
            
            x.StatusCodeShouldBe(204);
        });

        using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);
        order.Shipped.HasValue.ShouldBeTrue();
    }
    
    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_and_aggregate_but_no_command()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();

        await Scenario(x =>
        {
            x.Post.Url($"/orders/{status1.OrderId}/ship3");
            
            x.StatusCodeShouldBe(204);
        });

        using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);
        order.Shipped.HasValue.ShouldBeTrue();
    }
    
    [Fact]
    public async Task use_a_return_value_as_event_using_route_id_but_no_parameter_and_aggregate_but_no_command()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();

        await Scenario(x =>
        {
            x.Post.Url($"/orders/{status1.OrderId}/ship4");
            
            x.StatusCodeShouldBe(204);
        });

        using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);
        order.Shipped.HasValue.ShouldBeTrue();
    }
    
    [Fact]
    public async Task use_stream_collision_policy()
    {
        var id = Guid.NewGuid();
        
        // First time should be fine
        await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(id, new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create4");
        });
        
        // Second time hits an exception from stream id collision
        var result2 = await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(id, new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create4");
            x.StatusCodeShouldBe(400);
        });

        // And let's verify that we got what we expected for the ProblemDetails
        // in the HTTP response body of the 2nd request
        var details = result2.ReadAsJson<ProblemDetails>();
        Guid.Parse(details.Extensions["Id"].ToString()).ShouldBe(id);
        details.Detail.ShouldBe($"Duplicated id '{id}'");
    }
}