using System.Text.RegularExpressions;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Http.Tests;

// Reproduces GH-3282: a route template may contain characters that are legal in a URL path but not
// in a C# identifier (e.g. '$' in "/assets/$action"). Wolverine derives the generated handler *type
// name* from the route pattern (HttpChain.Codegen.determineFileName -> assembly.AddType(_fileName)),
// but only strips Path.GetInvalidPathChars() — which does NOT include '$' — so the generated type is
// named "GET_assets_$action" and codegen fails to compile with CS1056 "Unexpected character '$'".
public class special_characters_in_route_templates
{
    private static readonly Regex ValidCSharpIdentifier = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static HttpGraph BuildGraph()
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<WolverineHttpOptions>();
        registry.AddTransient<IServiceVariableSource>(c =>
            new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, JasperFx.RuntimeCompiler.AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();
        return new HttpGraph(
            new WolverineOptions { ApplicationAssembly = typeof(special_characters_in_route_templates).Assembly },
            container);
    }

    [Fact]
    public void generated_type_name_for_a_dollar_sign_route_is_a_valid_csharp_identifier()
    {
        var chain = HttpChain.ChainFor<RpcStyleEndpoint>(x => x.TriggerAction());

        // Description is the value handed verbatim to assembly.AddType() as the generated C# type name.
        var typeName = chain.Description;

        typeName.ShouldNotContain("$");
        ValidCSharpIdentifier.IsMatch(typeName)
            .ShouldBeTrue($"Generated type name '{typeName}' is not a valid C# identifier");
    }

    [Fact]
    public void endpoint_with_a_dollar_sign_route_compiles()
    {
        var parent = BuildGraph();
        var method = typeof(RpcStyleEndpoint).GetMethod(nameof(RpcStyleEndpoint.TriggerAction))!;
        var chain = new HttpChain(new MethodCall(typeof(RpcStyleEndpoint), method), parent);

        // Forces real code generation + compilation of just this endpoint. Before GH-3282 this threw a
        // CompilationException wrapping CS1056 "Unexpected character '$'".
        Should.NotThrow(() =>
            chain.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container.Services));
    }

    [Fact]
    public void type_name_override_wins_and_is_used()
    {
        var chain = HttpChain.ChainFor<RpcStyleEndpoint>(x => x.WithExplicitName());
        chain.Description.ShouldBe("AssetsRpcEndpoint");
    }

    [Fact]
    public void type_name_override_is_still_sanitized()
    {
        // Even an override can't smuggle an invalid identifier into codegen.
        var chain = HttpChain.ChainFor<RpcStyleEndpoint>(x => x.WithMessyName());
        chain.Description.ShouldNotContain("$");
        ValidCSharpIdentifier.IsMatch(chain.Description).ShouldBeTrue($"'{chain.Description}' is not a valid identifier");
    }

    [Fact]
    public void colliding_type_names_are_disambiguated_and_both_compile()
    {
        var parent = BuildGraph();

        var dollar = new HttpChain(
            new MethodCall(typeof(CollisionEndpoints), typeof(CollisionEndpoints).GetMethod(nameof(CollisionEndpoints.Dollar))!), parent);
        var dash = new HttpChain(
            new MethodCall(typeof(CollisionEndpoints), typeof(CollisionEndpoints).GetMethod(nameof(CollisionEndpoints.Dash))!), parent);

        // Both routes sanitize to the same generated type name before disambiguation.
        dollar.Description.ShouldBe(dash.Description);

        HttpGraph.ResolveDuplicateTypeNames([dollar, dash]);

        dollar.Description.ShouldNotBe(dash.Description);
        ValidCSharpIdentifier.IsMatch(dollar.Description).ShouldBeTrue();
        ValidCSharpIdentifier.IsMatch(dash.Description).ShouldBeTrue();

        // Both must still generate compilable code with their now-distinct names.
        Should.NotThrow(() =>
        {
            dollar.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container.Services);
            dash.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container.Services);
        });
    }

    [Fact]
    public void disambiguation_suffix_is_deterministic()
    {
        var parent1 = BuildGraph();
        var parent2 = BuildGraph();

        HttpChain Build(HttpGraph parent, string methodName) =>
            new(new MethodCall(typeof(CollisionEndpoints), typeof(CollisionEndpoints).GetMethod(methodName)!), parent);

        var a1 = Build(parent1, nameof(CollisionEndpoints.Dollar));
        var a2 = Build(parent1, nameof(CollisionEndpoints.Dash));
        HttpGraph.ResolveDuplicateTypeNames([a1, a2]);

        var b1 = Build(parent2, nameof(CollisionEndpoints.Dollar));
        var b2 = Build(parent2, nameof(CollisionEndpoints.Dash));
        HttpGraph.ResolveDuplicateTypeNames([b1, b2]);

        // Stable across runs — required for TypeLoadMode.Static codegen.
        a1.Description.ShouldBe(b1.Description);
        a2.Description.ShouldBe(b2.Description);
    }
}

public class RpcStyleEndpoint
{
    // '$action' marks an RPC-style route. Legal URL path, illegal C# identifier character.
    [WolverineGet("/assets/$action")]
    public string TriggerAction() => "triggered";

    // The escape hatch: name the generated type explicitly for an otherwise-awkward route.
    [WolverineGet("/assets/$explicit", TypeName = "AssetsRpcEndpoint")]
    public string WithExplicitName() => "explicit";

    // Even an override is sanitized.
    [WolverineGet("/assets/$messy", TypeName = "Assets$Rpc")]
    public string WithMessyName() => "messy";
}

public class CollisionEndpoints
{
    // Both of these routes sanitize to the same generated type name (GET_widgets_special).
    [WolverineGet("/widgets/$special")]
    public string Dollar() => "dollar";

    [WolverineGet("/widgets/-special")]
    public string Dash() => "dash";
}
