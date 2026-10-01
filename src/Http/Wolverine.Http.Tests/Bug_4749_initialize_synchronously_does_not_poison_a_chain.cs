using Alba;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4749. <c>HttpChain.AttachTypesSynchronously</c> assigned <c>_handlerType</c> unconditionally, so a
/// lookup that missed overwrote an already-resolved type with null -- and every subsequent request to that
/// route failed with "Failed to resolve the generated handler type for endpoint ..." for the life of the
/// host.
///
/// <para>A second <c>InitializeSynchronously</c> is guaranteed to miss: <c>DynamicTypeLoader.Initialize</c>
/// calls <c>StartAssembly</c> for a BRAND NEW <see cref="GeneratedAssembly" /> every time, and
/// <c>HttpChain.AssembleTypes</c> early-returns once <c>_generatedType</c> exists -- so the fresh assembly
/// is compiled empty and cannot contain this chain's handler. Under the default <c>AutoTypeLoader</c> it is
/// worse still: the probe against the application assembly nulls the type before any fallback runs.</para>
///
/// <para>Deliberately NOT an <see cref="IntegrationContext" />. Each fact stands up its own host so that no
/// fixture ordering -- and no earlier test's warm-up request -- can mask the poisoning.</para>
/// </summary>
public class Bug_4749_initialize_synchronously_does_not_poison_a_chain
{
    [Fact]
    public async Task initializing_before_the_first_request_leaves_the_route_usable()
    {
        await using var host = await startHostAsync();

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/gh4749/thing");
        chain.ShouldNotBeNull();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        chain.SourceCode.ShouldNotBeNull();

        // Pre-fix this threw out of buildHandler(): the request's own initialization probed a fresh,
        // empty GeneratedAssembly and nulled the type the line above had just resolved.
        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh4749Request("first")).ToUrl("/gh4749/thing");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("first");
    }

    [Fact]
    public async Task initializing_after_a_live_request_leaves_the_route_usable()
    {
        await using var host = await startHostAsync();

        await host.Scenario(x =>
        {
            x.Post.Json(new Gh4749Request("before")).ToUrl("/gh4749/thing");
            x.StatusCodeShouldBeOk();
        });

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/gh4749/thing");
        chain.ShouldNotBeNull();

        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);
        chain.SourceCode.ShouldNotBeNull();

        var response = await host.Scenario(x =>
        {
            x.Post.Json(new Gh4749Request("after")).ToUrl("/gh4749/thing");
            x.StatusCodeShouldBeOk();
        });

        (await response.ReadAsTextAsync()).ShouldBe("after");
    }

    private static async Task<IAlbaHost> startHostAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeAssembly(typeof(Bug_4749_initialize_synchronously_does_not_poison_a_chain).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-4749 endpoint", type => type != typeof(Gh4749Endpoint)))));
    }
}

public record Gh4749Request(string Name);

public static class Gh4749Endpoint
{
    [WolverinePost("/gh4749/thing")]
    public static string Post(Gh4749Request request) => request.Name;
}
