using System.Text.Json;
using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;

namespace Wolverine.Http.Tests;

public record JsonFormatResponse(string SomeName);

public static class JsonFormatEndpoints
{
    [WolverineGet("/json-format/application")]
    public static JsonFormatResponse Application() => new("hello");

    [WolverineGet("/json-format/own")]
    public static JsonFormatResponse Own() => new("hello");
}

// GH-5042. A library mapping its own endpoints into somebody else's application can keep its own wire format
public class per_endpoint_json_serializer_options : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // Marten is here only because assembly wide endpoint discovery also picks up endpoints that need a
        // document store to codegen against
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "json_format";
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts =>
            {
                #region sample_per_endpoint_json_serializer_options
                opts.ConfigureEndpoints(chain =>
                {
                    if (chain.Method.Method.Name == nameof(JsonFormatEndpoints.Own))
                    {
                        // This endpoint writes PascalCase, whatever the application configures
                        chain.JsonSerializerOptions = new JsonSerializerOptions { PropertyNamingPolicy = null };
                    }
                });
                #endregion
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
    public async Task endpoints_use_the_application_json_options_by_default()
    {
        var result = await theHost.GetAsText("/json-format/application");
        result.ShouldContain("\"someName\"");
    }

    [Fact]
    public async Task an_endpoint_can_use_its_own_json_options()
    {
        var result = await theHost.GetAsText("/json-format/own");
        result.ShouldContain("\"SomeName\"");
    }
}
