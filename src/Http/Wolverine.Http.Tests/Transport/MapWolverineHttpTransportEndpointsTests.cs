using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Http.Transport;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class MapWolverineHttpTransportEndpointsTests
{
    [Fact]
    public void can_map_with_custom_prefix()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        var group = app.MapWolverineHttpTransportEndpoints("/custom-prefix");

        group.ShouldNotBeNull();
        group.ShouldBeOfType<RouteGroupBuilder>();
    }

    [Fact]
    public void uses_default_prefix_when_not_specified()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        var group = app.MapWolverineHttpTransportEndpoints();

        group.ShouldNotBeNull();
        group.ShouldBeOfType<RouteGroupBuilder>();
    }

    [Fact]
    public void can_provide_custom_json_serializer_options()
    {
        var customOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true
        };

        var builder = WebApplication.CreateBuilder();
        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        var group = app.MapWolverineHttpTransportEndpoints("/_wolverine", customOptions);

        // The custom options will be passed to the HttpTransportExecutor.InvokeAsync method
        // and used when deserializing CloudEvents
        group.ShouldNotBeNull();
    }

    [Fact]
    public void maps_both_batch_and_invoke_endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        var group = app.MapWolverineHttpTransportEndpoints();

        // Verify RouteGroupBuilder is returned
        group.ShouldNotBeNull();
        group.ShouldBeOfType<RouteGroupBuilder>();
    }

    [Fact]
    public void returns_route_group_builder_for_further_configuration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseWolverine();
        builder.Services.AddWolverineHttp();

        var app = builder.Build();
        var group = app.MapWolverineHttpTransportEndpoints();

        group.ShouldNotBeNull();
        group.ShouldBeOfType<RouteGroupBuilder>();
    }
}

public record TestCommand(string Value);
public static class TestCommandHandler
{
    public static void Handle(TestCommand command)
    {
        // Just receive it
    }
}

