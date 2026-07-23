using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PingPongWithGrpc.Ponger;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-3591: mapping Wolverine gRPC services in the same host as Wolverine HTTP endpoints made every
// HTTP route 404. Commenting out MapWolverineGrpcServices() brought HTTP back, so the two mappings
// were not coexisting. These tests stand up one host with both — in the exact registration and
// mapping order from the issue — and assert the HTTP route responds.
public class grpc_and_http_coexistence_3591 : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(PingGrpcService).Assembly;
            opts.Discovery.IncludeAssembly(typeof(grpc_and_http_coexistence_3591).Assembly);
        });

        // The issue's registration order, verbatim.
        builder.Services.AddGrpc();
        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc(_ => { });
        builder.Services.AddWolverineHttp();
        builder.Services.AddSingleton<PingTracker>();

        _app = builder.Build();

        _app.UseRouting();

        // The issue's mapping order: gRPC services before Wolverine HTTP endpoints, with the
        // FluentValidation ProblemDetails middleware configured.
        _app.MapWolverineGrpcServices();
        _app.MapWolverineEndpoints(o => o.UseFluentValidationProblemDetailMiddleware());

        await _app.StartAsync();

        _client = _app.GetTestServer().CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task http_get_still_responds_when_grpc_services_are_mapped()
    {
        var response = await _client.GetAsync("/api/coexist/check");

        response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.NotFound);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync()).ShouldContain("ok");
    }

    [Fact]
    public async Task http_get_with_asparameters_and_invoke_still_responds_when_grpc_is_mapped()
    {
        // The issue's exact endpoint shape: [AsParameters] request forwarded through the message bus.
        var response = await _client.GetAsync("/api/coexist/invoke?Name=bob");

        response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.NotFound);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync()).ShouldContain("bob");
    }
}

public static class CoexistenceHttpEndpoint
{
    [WolverineGet("/api/coexist/check")]
    public static string GetCheck() => "ok";

    [WolverineGet("/api/coexist/invoke")]
    public static Task<CoexistCheckResponse> GetCheck([AsParameters] CoexistCheckRequest req, IMessageBus bus)
        => bus.InvokeAsync<CoexistCheckResponse>(req);
}

// A bindable [AsParameters] type: parameterless ctor + a [FromQuery] settable property, so the
// HTTP chain compiles and the querystring binds. (Properties with no source attribute are not bound.)
public class CoexistCheckRequest
{
    [FromQuery]
    public string Name { get; set; } = string.Empty;
}

public record CoexistCheckResponse(string Message);

public static class CoexistCheckHandler
{
    public static CoexistCheckResponse Handle(CoexistCheckRequest req) => new($"hello {req.Name}");
}
