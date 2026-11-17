using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Alba;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Runtime;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-5406. The request span carries <c>handler.type</c>, <c>http.route</c> and <c>http.request.method</c> even
/// when the request is turned away before the endpoint runs: by tenant detection, by an unreadable JSON body,
/// or by validation. Those are the requests an operator most often wants to group by route.
/// </summary>
public class request_span_tags_on_short_circuits_5406 : IAsyncLifetime
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
            app.MapWolverineEndpoints(opts =>
            {
                opts.CustomizeHttpEndpointDiscovery(q =>
                    q.Excludes.WithCondition("Not the GH-5406 endpoints", type => type != typeof(SpanTagged5406Endpoints)));

                // Every one of these inserts its frame at the front of the chain after the span tag policy ran
                opts.TenantId.IsRequestHeaderValue("Tenant");
                opts.TenantId.AssertExists();
                opts.UseDataAnnotationsValidationProblemDetailMiddleware();
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

    private Activity theRequestSpan()
    {
        return _stopped
            .Where(x => x.GetTagItem(WolverineTracing.HandlerType) as string == typeof(SpanTagged5406Endpoints).FullName)
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task a_request_without_a_tenant_id_is_still_tagged()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new SpanTagged5406Note("hello")).ToUrl("/api/span-tags-5406/orders/42/notes");
            x.StatusCodeShouldBe(400);
        });

        var request = theRequestSpan();
        request.GetTagItem("http.route").ShouldBe("/api/span-tags-5406/orders/{id}/notes");
        request.GetTagItem("http.request.method").ShouldBe("POST");
    }

    [Fact]
    public async Task a_request_with_an_unreadable_body_is_still_tagged()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Text("{ this is not json").ToUrl("/api/span-tags-5406/orders/42/notes");
            x.WithRequestHeader("Content-Type", "application/json");
            x.WithRequestHeader("Tenant", "one");
            x.StatusCodeShouldBe(400);
        });

        var request = theRequestSpan();
        request.GetTagItem("http.route").ShouldBe("/api/span-tags-5406/orders/{id}/notes");
        request.GetTagItem("http.request.method").ShouldBe("POST");
    }

    [Fact]
    public async Task a_request_that_fails_validation_is_still_tagged()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new SpanTagged5406Note(null)).ToUrl("/api/span-tags-5406/orders/42/notes");
            x.WithRequestHeader("Tenant", "one");
            x.StatusCodeShouldBe(400);
        });

        var request = theRequestSpan();
        request.GetTagItem("http.route").ShouldBe("/api/span-tags-5406/orders/{id}/notes");
        request.GetTagItem("http.request.method").ShouldBe("POST");
    }

    [Fact]
    public async Task a_request_that_succeeds_is_tagged_once()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new SpanTagged5406Note("hello")).ToUrl("/api/span-tags-5406/orders/42/notes");
            x.WithRequestHeader("Tenant", "one");
            x.StatusCodeShouldBe(204);
        });

        theRequestSpan().GetTagItem("http.route").ShouldBe("/api/span-tags-5406/orders/{id}/notes");
    }

    [Fact]
    public async Task the_tags_are_the_first_statement_of_the_generated_handler()
    {
        // The handler is generated on the first request
        await theHost.Scenario(x =>
        {
            x.Post.Json(new SpanTagged5406Note("hello")).ToUrl("/api/span-tags-5406/orders/42/notes");
            x.StatusCodeShouldBe(400);
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.Method.Method.Name == nameof(SpanTagged5406Endpoints.PostNote));

        var code = chain.SourceCode.ShouldNotBeNull();
        var tags = code.IndexOf("SetTag(\"http.route\"", StringComparison.Ordinal);
        tags.ShouldBeGreaterThan(0);

        tags.ShouldBeLessThan(code.IndexOf("TryDetectTenantId", StringComparison.Ordinal));
        tags.ShouldBeLessThan(code.IndexOf("ReadJsonAsync", StringComparison.Ordinal));
    }
}

public record SpanTagged5406Note([property: Required] string? Text);

public static class SpanTagged5406Endpoints
{
    [WolverinePost("/api/span-tags-5406/orders/{id}/notes"), EmptyResponse]
    public static void PostNote(int id, SpanTagged5406Note note)
    {
    }
}
