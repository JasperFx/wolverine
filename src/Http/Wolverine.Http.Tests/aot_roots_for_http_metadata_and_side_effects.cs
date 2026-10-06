using Alba;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
/// GH-4825. Two reflective closes on the HTTP startup path had no AOT root, and a Marten-backed native
/// image hit both before it could serve a request:
///
/// <para><c>SideEffectPolicy</c> asks every <see cref="ISideEffect" /> return type for a public
/// <c>Execute</c>/<c>ExecuteAsync</c>. It is a policy, so it runs at startup under
/// <see cref="TypeLoadMode.Static" /> as well, and a trimmed method's metadata makes that lookup answer
/// null — <c>InvalidSideEffectException</c> naming <c>Wolverine.Marten.IStartStream</c>.</para>
///
/// <para><c>HttpChain.tryApplyAsEndpointMetadataProvider</c> closes <see cref="HttpChain.Applier{T}" />
/// over the endpoint's resource type. That is a DIFFERENT <c>Applier&lt;T&gt;</c> from the
/// <c>Wolverine.Configuration</c> one GH-4778 rooted, and what lands on it is as ordinary as it gets: an
/// endpoint returning <c>Results&lt;Ok&lt;T&gt;, ProblemHttpResult&gt;</c>.</para>
///
/// <para>Deliberately not an <c>IntegrationContext</c>: the endpoints and the side-effect interfaces are
/// local to this file and excluded from every other host's discovery, for the same reason Bug_4789's are.</para>
/// </summary>
public class aot_roots_for_http_metadata_and_side_effects
{
    [Fact]
    public async Task the_chain_records_the_type_it_closed_the_metadata_applier_over()
    {
        await using var host = await startHostAsync();

        var chain = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!
            .ChainFor("GET", "/gh4825/typed");
        chain.ShouldNotBeNull();

        // Not vacuous, and the reason this is asserted separately: the rooting block is built from what
        // the chain says it closed. A recording that never happens emits no root, no warning and no
        // failure until somebody publishes a native image.
        chain.EndpointMetadataProviderTypes
            .ShouldContain(typeof(Results<Ok<Gh4825Response>, ProblemHttpResult>));
    }

    [Fact]
    public async Task roots_both_the_metadata_applier_and_the_side_effect_interfaces()
    {
        await using var host = await startHostAsync();

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;

        // The rooting block is only emitted while actually generating code -- the same guard CoreTests'
        // aot_root_contribution uses.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        string code;
        try
        {
            var builder = new DynamicCodeBuilder(host.Services, [graph])
            {
                ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
            };

            code = builder.GenerateAllCode();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }

        // The closed Applier<T>. The open generic could not be named in a [DynamicDependency] at all, and
        // the resource type being rooted on its own does not create the instantiation.
        code.ShouldContain(
            "typeof(global::Wolverine.Http.HttpChain.Applier<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<Wolverine.Http.Tests.Gh4825Response>, Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>>)");

        // The side effect's declared return type...
        code.ShouldContain("typeof(global::Wolverine.Http.Tests.IGh4825DerivedSideEffect)");

        // ...and the interface that actually declares Execute, which is the half that fixes the reported
        // failure: IStartStream inherits Execute from IMartenOp, so rooting only the return type keeps the
        // interface list and loses the method findMethod then looks for.
        code.ShouldContain("typeof(global::Wolverine.Http.Tests.IGh4825SideEffect)");
    }

    [Fact]
    public void codegen_refuses_to_run_while_a_chain_has_no_built_endpoint()
    {
        // GH-4841. EndpointMetadataProviderTypes is recorded by BuildEndpoint, so a rooting block generated
        // before DiscoverEndpoints built the endpoints would be silently short of every
        // IEndpointMetadataProvider root -- a native image that fails at startup, with nothing reporting it
        // sooner. HttpGraph.Add is the one way to get a chain into the graph without building it.
        var graph = new HttpGraph(new WolverineOptions(), ServiceContainer.Empty());
        var chain = graph.Add(new MethodCall(typeof(Gh4825Endpoints), nameof(Gh4825Endpoints.Typed)),
            HttpMethod.Get, "/gh4825/typed");

        // The precondition the guard is about, stated so that the throw below is not for some other reason.
        chain.Endpoint.ShouldBeNull();

        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var exception = Should.Throw<InvalidOperationException>(() => ((ICodeFileCollection)graph).BuildFiles());

            // Named, because the operator fixing this needs to know which chain, not that one exists.
            exception.Message.ShouldContain(chain.ToString());
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void static_attach_still_enumerates_the_files_before_the_endpoints_are_built()
    {
        // The negative control for the guard above. AssertPreBuiltTypesExist walks BuildFiles during
        // TypeLoadMode.Static attach, which DiscoverEndpoints runs BEFORE BuildEndpoint -- and that path
        // emits no rooting block, so it has nothing to be short of. A guard that fired there would turn
        // every Static-mode HTTP application into a startup failure.
        var graph = new HttpGraph(new WolverineOptions(), ServiceContainer.Empty());
        graph.Add(new MethodCall(typeof(Gh4825Endpoints), nameof(Gh4825Endpoints.Typed)), HttpMethod.Get,
            "/gh4825/typed");

        DynamicCodeBuilder.WithinCodegenCommand.ShouldBeFalse();

        ((ICodeFileCollection)graph).BuildFiles().OfType<HttpChain>().ShouldNotBeEmpty();
    }

    private static async Task<IAlbaHost> startHostAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.MediatorOnly;
            opts.Discovery.DisableConventionalDiscovery();

            // Explicit, not inferred, for the reason Bug_4789's host spells out: once other hosts in this
            // assembly have started, application-assembly inference resolves elsewhere and discovery finds
            // nothing.
            opts.Discovery.IncludeAssembly(typeof(aot_roots_for_http_metadata_and_side_effects).Assembly);
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a GH-4825 endpoint", type => type != typeof(Gh4825Endpoints)))));
    }
}

public record Gh4825Response(string Name);

/// <summary>
///     Declares <c>Execute</c>, so this is the interface <c>SideEffectPolicy.findMethod</c> finally finds
///     it on — the shape of <c>Wolverine.Marten.IMartenOp</c>.
/// </summary>
public interface IGh4825SideEffect : ISideEffect
{
    void Execute();
}

/// <summary>
///     The declared return type, which declares nothing of its own — the shape of
///     <c>Wolverine.Marten.IStartStream</c>.
/// </summary>
public interface IGh4825DerivedSideEffect : IGh4825SideEffect;

public class Gh4825SideEffect : IGh4825DerivedSideEffect
{
    public void Execute()
    {
    }
}

public static class Gh4825Endpoints
{
    [WolverineGet("/gh4825/typed")]
    public static Results<Ok<Gh4825Response>, ProblemHttpResult> Typed()
    {
        return TypedResults.Ok(new Gh4825Response("ok"));
    }

    [WolverinePost("/gh4825/side-effect")]
    public static IGh4825DerivedSideEffect SideEffect()
    {
        return new Gh4825SideEffect();
    }
}
