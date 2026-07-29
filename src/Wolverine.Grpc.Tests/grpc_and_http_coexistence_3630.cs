using GreeterProtoFirstGrpc.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-3630, the continuation of GH-3591: HTTP routes 404 when Wolverine gRPC services are also mapped.
// grpc_and_http_coexistence_3591 could not reproduce it, and this fixture is the issue's attached repro
// narrowed down. It differs from the 3591 fixture on the axes the reporter's Program.cs actually differs on:
//
//   * NO explicit app.UseRouting() -- WebApplication's implicit routing is used instead
//   * a PROTO-FIRST abstract [WolverineGrpcService] stub over stock Grpc.AspNetCore, not code-first
//     ProtoBuf.Grpc via AddCodeFirstGrpc()
//   * no pinned opts.ApplicationAssembly
public class grpc_and_http_coexistence_3630 : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(GreeterGrpcService).Assembly);
            opts.Discovery.IncludeAssembly(typeof(grpc_and_http_coexistence_3630).Assembly);
        });

        // The issue's registration order, verbatim: stock gRPC + Wolverine gRPC + Wolverine HTTP.
        builder.Services.AddGrpc();
        builder.Services.AddWolverineGrpc();
        builder.Services.AddWolverineHttp();

        _app = builder.Build();

        // Deliberately NO _app.UseRouting() -- that is what the reporter's Program.cs does.
        _app.MapWolverineGrpcServices();
        _app.MapWolverineEndpoints(o => o.UseFluentValidationProblemDetailMiddleware());

        await _app.StartAsync();

        _client = _app.GetTestServer().CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task http_get_still_responds_when_grpc_services_are_mapped()
    {
        var response = await _client.GetAsync("/gh3630/check");

        response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.NotFound);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync()).ShouldContain("ok");
    }

    [Fact]
    public async Task http_get_with_asparameters_and_invoke_still_responds_when_grpc_is_mapped()
    {
        // The issue's exact endpoint shape: an [AsParameters] request forwarded through the message bus.
        var response = await _client.GetAsync("/gh3630/invoke?Name=bob&Pin=1111");

        response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.NotFound);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync()).ShouldContain("bob");
    }
}

public static class Coexistence3630HttpEndpoint
{
    [WolverineGet("/gh3630/check")]
    public static string GetCheck() => "ok";

    [WolverineGet("/gh3630/invoke")]
    public static Task<Coexist3630CheckResponse> GetInvoke([AsParameters] Coexist3630CheckRequest req,
        IMessageBus bus)
        => bus.InvokeAsync<Coexist3630CheckResponse>(req);
}

// The repro's exact shape: a POSITIONAL RECORD whose constructor parameters carry [FromQuery],
// not a class with settable properties.
public record Coexist3630CheckRequest([FromQuery] string? Name, [FromQuery] string? Pin);

public record Coexist3630CheckResponse(string Message);

public static class Coexist3630CheckHandler
{
    public static Coexist3630CheckResponse Handle(Coexist3630CheckRequest req) => new($"hello {req.Name}");
}

