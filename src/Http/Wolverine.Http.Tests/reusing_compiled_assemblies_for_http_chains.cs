using System.Reflection;
using Alba;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests;

public class ReuseCompiledEndpoint
{
    [WolverineGet("/reuse-compiled/hello")]
    public static string Get() => "hello";
}

// GH-5391: GenerationRules.ReuseCompiledAssemblies (jasperfx#1114) proven for HTTP endpoint chains
public class reusing_compiled_assemblies_for_http_chains
{
    private static async Task<IAlbaHost> startHost(bool reuse, TypeLoadMode mode)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(ReuseCompiledEndpoint).Assembly);
            opts.CodeGeneration.TypeLoadMode = mode;
            opts.CodeGeneration.ReuseCompiledAssemblies = reuse;
        });
        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints(opts => opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not the GH-5391 endpoint", type => type != typeof(ReuseCompiledEndpoint))));
        });
    }

    private static Assembly compiledAssembly(IAlbaHost host)
    {
        var chain = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == "/reuse-compiled/hello");

        var field = typeof(HttpChain).GetField("_handlerType", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((Type)field.GetValue(chain)!).Assembly;
    }

    private static async Task assertWorks(IAlbaHost host)
    {
        var result = await host.Scenario(x =>
        {
            x.Get.Url("/reuse-compiled/hello");
            x.StatusCodeShouldBeOk();
        });
        (await result.ReadAsTextAsync()).ShouldBe("hello");
    }

    [Theory]
    [InlineData(TypeLoadMode.Dynamic)]
    [InlineData(TypeLoadMode.Auto)]
    public async Task two_hosts_share_the_compiled_endpoint_assembly_when_on(TypeLoadMode mode)
    {
        await using var one = await startHost(true, mode);
        await using var two = await startHost(true, mode);
        await assertWorks(one);
        await assertWorks(two);

        compiledAssembly(one).ShouldBeSameAs(compiledAssembly(two));
    }

    [Fact]
    public async Task two_hosts_compile_separately_by_default()
    {
        await using var one = await startHost(false, TypeLoadMode.Dynamic);
        await using var two = await startHost(false, TypeLoadMode.Dynamic);
        await assertWorks(one);
        await assertWorks(two);

        compiledAssembly(one).ShouldNotBeSameAs(compiledAssembly(two));
    }
}
