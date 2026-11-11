using System.Collections.Concurrent;
using System.Diagnostics;
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5332. The request span of a Wolverine endpoint carries the OpenTelemetry <c>http.route</c> and
/// <c>http.request.method</c> tags as well as <c>handler.type</c>, so a host without the ASP.NET Core
/// instrumentation can still tell which endpoint served a request.
/// </summary>
public class request_span_route_and_method_tags_5332 : IAsyncLifetime
{
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            app.UseDeveloperExceptionPage();
            app.MapWolverineEndpoints(opts =>
            {
                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the GH-5332 endpoints", type => type != typeof(SpanTagged5332Endpoints)));

                // No attribute maps a method to several HTTP methods, so the test widens one chain's
                // generated code itself. ASP.NET Core's routing metadata was built with the chain, so the
                // route still answers POST only.
                opts.ConfigureEndpoints(chain =>
                {
                    if (chain.Method.Method.Name == nameof(SpanTagged5332Endpoints.PostOrPut))
                    {
                        chain.MapToRoute("PUT", chain.RoutePattern!.RawText!);
                    }
                });
            });
        });
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        _listener.Dispose();
        if (theHost != null)
        {
            await theHost.StopAsync();
            theHost.Dispose();
        }
    }

    [Fact]
    public async Task the_request_span_carries_the_route_pattern_and_the_method()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Url("/api/span-tags-5332/orders/42/notes");
            x.StatusCodeShouldBe(204);
        });

        var request = _stopped
            .Where(x => x.GetTagItem(WolverineTracing.HandlerType) as string == typeof(SpanTagged5332Endpoints).FullName)
            .ShouldHaveSingleItem();

        request.GetTagItem("http.route").ShouldBe("/api/span-tags-5332/orders/{id}/notes");
        request.GetTagItem("http.request.method").ShouldBe("POST");
    }

    [Fact]
    public async Task a_chain_with_several_methods_tags_the_method_of_the_request()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Url("/api/span-tags-5332/either");
            x.StatusCodeShouldBe(204);
        });

        var requests = _stopped
            .Where(x => x.GetTagItem(WolverineTracing.HandlerType) as string == typeof(SpanTagged5332Endpoints).FullName)
            .ToArray();

        var request = requests.ShouldHaveSingleItem();
        request.GetTagItem("http.route").ShouldBe("/api/span-tags-5332/either");
        request.GetTagItem("http.request.method").ShouldBe("POST");

        // Read from the request rather than written as a constant
        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.Method.Name == nameof(SpanTagged5332Endpoints.PostOrPut));
        var code = chain.SourceCode.ShouldNotBeNull();
        code.ShouldContain("SetTag(\"http.request.method\", httpContext.Request.Method)");
    }

    [Fact]
    public async Task a_single_method_is_a_constant_in_the_generated_code()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Url("/api/span-tags-5332/orders/42/notes");
            x.StatusCodeShouldBe(204);
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.Method.Name == nameof(SpanTagged5332Endpoints.PostNote));

        var code = chain.SourceCode.ShouldNotBeNull();
        code.ShouldContain("SetTag(\"http.request.method\", \"POST\")");
        code.ShouldContain("SetTag(\"http.route\", \"/api/span-tags-5332/orders/{id}/notes\")");
    }
}

public static class SpanTagged5332Endpoints
{
    [WolverinePost("/api/span-tags-5332/orders/{id}/notes"), EmptyResponse]
    public static void PostNote(int id)
    {
    }

    [WolverinePost("/api/span-tags-5332/either"), EmptyResponse]
    public static void PostOrPut()
    {
    }
}
