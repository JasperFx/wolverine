using System.Collections.Concurrent;
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5892. A synchronous endpoint that binds an <c>[AsParameters]</c> type with a <c>[FromBody]</c> member still
/// reads the request body asynchronously, so its generated handler has to be async like one with a plain
/// <c>[FromBody]</c> parameter. It used to stay synchronous and fail to compile.
/// </summary>
public class synchronous_as_parameters_body_5892 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the synchronous [AsParameters] body endpoints",
                        type => type != typeof(SynchronousAsParametersBodyEndpoints)));
            });
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task a_void_endpoint_with_a_body_constructor_parameter()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Put.Json(new RenamePayload("Wolverine")).ToUrl($"/api/sync-as-parameters/things/{id}/name");
            x.StatusCodeShouldBe(204);
        });

        SynchronousAsParametersBodyEndpoints.Renamed.ShouldContain($"{id}:Wolverine");
    }

    [Fact]
    public async Task a_void_endpoint_with_a_body_property()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Put.Json(new RenamePayload("Marten")).ToUrl($"/api/sync-as-parameters/things/{id}/label");
            x.StatusCodeShouldBe(204);
        });

        SynchronousAsParametersBodyEndpoints.Renamed.ShouldContain($"{id}:Marten");
    }

    [Fact]
    public async Task a_synchronous_endpoint_that_returns_a_value()
    {
        var id = Guid.NewGuid();

        var result = await theHost.Scenario(x =>
        {
            x.Put.Json(new RenamePayload("Polecat")).ToUrl($"/api/sync-as-parameters/things/{id}/echo");
        });

        (await result.ReadAsTextAsync()).ShouldBe($"{id}:Polecat");
    }
}

public record RenamePayload(string Name);

public record RenameCommand([FromRoute] Guid Id, [FromBody] RenamePayload Body);

public class RelabelCommand
{
    [FromRoute] public Guid Id { get; set; }

    [FromBody] public RenamePayload Body { get; set; } = null!;
}

public static class SynchronousAsParametersBodyEndpoints
{
    public static readonly ConcurrentQueue<string> Renamed = new();

    [EmptyResponse]
    [WolverinePut("/api/sync-as-parameters/things/{id}/name")]
    public static void Rename([AsParameters] RenameCommand command)
        => Renamed.Enqueue($"{command.Id}:{command.Body.Name}");

    [EmptyResponse]
    [WolverinePut("/api/sync-as-parameters/things/{id}/label")]
    public static void Relabel([AsParameters] RelabelCommand command)
        => Renamed.Enqueue($"{command.Id}:{command.Body.Name}");

    [WolverinePut("/api/sync-as-parameters/things/{id}/echo")]
    public static string Echo([AsParameters] RenameCommand command) => $"{command.Id}:{command.Body.Name}";
}
