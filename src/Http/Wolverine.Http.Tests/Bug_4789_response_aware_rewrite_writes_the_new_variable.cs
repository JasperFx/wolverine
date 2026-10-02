using Alba;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Configuration;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4789. <see cref="IChain.UseForResponse" /> replaces a chain's response — it is how an
/// <see cref="IResponseAware" /> such as Marten's <c>UpdatedAggregate</c> swaps the declared response for
/// another call's return value — and it sets <c>ResourceType</c>, <c>ResourceVariable</c> and a
/// postprocessor to do it.
///
/// <para>Three of the five resource writer policies gated on the NEW <c>ResourceType</c> while writing the
/// handler method's OWN first created variable, so after a rewrite they wrote the wrong one. For a rewrite
/// to a string that produced generated code which did not compile:
/// <c>WriteString(httpContext, theMarker, 404)</c> against a <c>string?</c> parameter, CS1503.
/// <c>JsonResourceWriterPolicy</c> and <c>ContentNegotiationPolicy</c> already read
/// <c>ResourceVariable</c> first, which is why the JSON path — and therefore <c>UpdatedAggregate</c> —
/// worked while the string, <see cref="Microsoft.AspNetCore.Http.IResult" /> and status-code paths did
/// not.</para>
///
/// <para>Deliberately not an <c>IntegrationContext</c>: the endpoint and the IResponseAware are local to
/// this file and excluded from every other host's discovery, because an <c>IResponseAware</c> visible to
/// the shared WolverineWebApi hosts would change their endpoints' declared responses.</para>
/// </summary>
public class Bug_4789_response_aware_rewrite_writes_the_new_variable
{
    [Fact]
    public async Task a_rewritten_string_response_writes_the_rewritten_value()
    {
        await using var host = await startHostAsync();

        // Pre-fix the chain did not compile at all, so this request failed rather than returning a body.
        var response = await host.Scenario(x =>
        {
            x.Post.Url("/gh4789/rewritten");
            x.StatusCodeShouldBeOk();
        });

        // The handler returns Gh4789Marker; ConfigureResponse replaced the response with Describe().
        // Asserting the rewritten VALUE, not merely a 200: writing the handler's own variable was the bug,
        // so a test that only checked the status code would pass against it once it compiled.
        (await response.ReadAsTextAsync()).ShouldBe("rewritten-by-configure-response");
    }

    [Fact]
    public async Task the_chain_reports_the_rewritten_resource_type()
    {
        await using var host = await startHostAsync();

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/gh4789/rewritten");
        chain.ShouldNotBeNull();

        // Not vacuous: proves UseForResponse really did take effect, so the test above is exercising the
        // rewritten path rather than an endpoint that happened to return a string of its own.
        chain.ResourceType.ShouldBe(typeof(string));
        chain.ResourceVariable!.VariableType.ShouldBe(typeof(string));
    }

    private static async Task<IAlbaHost> startHostAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.Discovery.DisableConventionalDiscovery();

            // Explicit, not inferred. Without it these two facts pass in isolation and 404 in a full suite
            // run: the application-assembly inference resolves differently once other hosts in this
            // assembly have already started, so endpoint discovery found nothing. Bug_4749's host does the
            // same thing for the same reason.
            opts.Discovery.IncludeAssembly(
                typeof(Bug_4789_response_aware_rewrite_writes_the_new_variable).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-4789 endpoint", type => type != typeof(Gh4789Endpoint)))));
    }
}

public class Gh4789Marker : IResponseAware
{
    public static void ConfigureResponse(IChain chain)
    {
        chain.UseForResponse(MethodCall.For<Gh4789Describer>(x => Gh4789Describer.Describe()));
    }
}

// Not a static class: MethodCall.For<T> takes T as a type argument, which a static type cannot be.
public class Gh4789Describer
{
    public static string Describe() => "rewritten-by-configure-response";
}

public static class Gh4789Endpoint
{
    [WolverinePost("/gh4789/rewritten")]
    public static Gh4789Marker Post() => new();
}
