using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
///     GH-4825. <c>SideEffectPolicy</c> asks each <see cref="ISideEffect" /> return type for a public
///     <c>Execute</c>/<c>ExecuteAsync</c> while the chains are being built. It is a POLICY, so that happens
///     at startup in a native image too — <see cref="TypeLoadMode.Static" /> spares the code
///     <em>generation</em>, not the chain model — and the generated handler's direct call to
///     <c>Execute</c> preserves the method without preserving the metadata a <c>GetMethod</c> needs. A
///     Marten-backed application reported the result: <c>InvalidSideEffectException</c> naming
///     <c>Wolverine.Marten.IStartStream</c>, before the host ever started.
///     <para>
///     GH-4840. Which interfaces get rooted alongside the return type is decided by the same test
///     <c>findMethod</c> applies — does the interface DECLARE <c>Execute</c>/<c>ExecuteAsync</c> — rather
///     than by castability to <see cref="ISideEffect" />, which agreed with it on <c>IStartStream</c> and
///     not in general. The last three tests here are the shapes where the two answers differ.
///     </para>
/// </summary>
public class aot_roots_for_side_effects
{
    [Fact]
    public void roots_the_side_effect_return_type_and_the_interface_declaring_execute()
    {
        // The rooting block is only emitted while actually generating code -- the same guard as
        // aot_root_contribution and the message-type scan.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var code = generateAllCode(typeof(SideEffectRootSample));

            // The declared return type, which is what findMethod is handed.
            code.ShouldContain("typeof(global::CoreTests.Configuration.IDerivedSideEffect)");

            // And the interface findMethod recurses into. This is the half that actually fixes the
            // reported failure: IStartStream declares no Execute of its own either -- it inherits it from
            // IMartenOp -- so rooting only the return type keeps the interface list and loses the method.
            code.ShouldContain("typeof(global::CoreTests.Configuration.IRootedSideEffect)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void does_not_root_the_member_less_marker_interfaces_under_isideeffect()
    {
        // ISideEffect, IWolverineReturnType and INotToBeRouted declare no members at all, so an entry for
        // any of them would be pure noise in a block that every AOT application carries and the codegen
        // drift gate byte-compares. The castability filter this replaced excluded the two base markers only
        // as a side effect of them being BASES of ISideEffect; the declares-Execute test excludes them for
        // the reason that matters, and this is what keeps them out if the filter changes again.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var code = generateAllCode(typeof(SideEffectRootSample));

            code.ShouldNotContain("typeof(global::Wolverine.ISideEffect)");
            code.ShouldNotContain("typeof(global::Wolverine.Configuration.IWolverineReturnType)");
            code.ShouldNotContain("typeof(global::Wolverine.Runtime.INotToBeRouted)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void does_not_root_a_marker_interface_between_the_return_type_and_the_declaring_one()
    {
        // GH-4840. IDeeperSideEffect -> IDerivedSideEffect -> IRootedSideEffect, and only the last declares
        // Execute. findMethod walks straight past IDerivedSideEffect because it declares nothing, so there
        // is nothing on it for a native image to need. Castability would have rooted it anyway.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var code = generateAllCode(typeof(SideEffectMarkerSample));

            code.ShouldContain("typeof(global::CoreTests.Configuration.IDeeperSideEffect)");
            code.ShouldContain("typeof(global::CoreTests.Configuration.IRootedSideEffect)");

            code.ShouldNotContain("typeof(global::CoreTests.Configuration.IDerivedSideEffect)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void roots_an_interface_declaring_execute_that_is_not_itself_a_side_effect()
    {
        // GH-4840. The other direction. IExecutesOutsideWolverine declares Execute and does not inherit
        // ISideEffect; IOutsideDeclaredSideEffect inherits both and declares nothing. findMethod walks EVERY
        // interface of the return type, so IExecutesOutsideWolverine is where it finds the method -- and
        // the castability filter would not have rooted it at all, which in a native image is the reported
        // InvalidSideEffectException with a different interface named in it.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            // Not vacuous: GenerateAllCode runs SideEffectPolicy, so this would already have thrown if the
            // policy could not find Execute through that interface on CoreCLR.
            var code = generateAllCode(typeof(SideEffectOutsideSample));

            code.ShouldContain("typeof(global::CoreTests.Configuration.IOutsideDeclaredSideEffect)");
            code.ShouldContain("typeof(global::CoreTests.Configuration.IExecutesOutsideWolverine)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void a_side_effect_return_type_is_not_a_published_type()
    {
        // GH-4840. Found by the Fisher native lane the first time it returned a side-effect interface
        // declared in the application: PublishedTypes() listed it, AllMessageTypes() therefore handed it to
        // PrepopulateRoutingCache at startup, and the EmptyMessageRouter<T> closed over it reflectively had
        // no native code. A side effect is consumed by its policy inside the chain; it is never routed and
        // nothing can root a router for it.
        using var host = buildHost(typeof(SideEffectMarkerSample));
        _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var chain = graph.ChainFor(typeof(SideEffectMarkerMessage)).ShouldNotBeNull();

        chain.PublishedTypes().ShouldNotContain(typeof(IDeeperSideEffect));
        graph.AllMessageTypes().ShouldNotContain(typeof(IDeeperSideEffect));

        // Not vacuous: the chain really does return it.
        chain.ReturnVariablesOfType(typeof(ISideEffect)).Select(x => x.VariableType)
            .ShouldContain(typeof(IDeeperSideEffect));
    }

    [Fact]
    public void collects_nothing_outside_codegen()
    {
        // BuildFiles is enumerated during TypeLoadMode.Static ATTACH as well, where no rooting block is
        // emitted. Walking GetInterfaces() over every side effect there would be reflection a native image
        // pays for at startup to build a list that is then thrown away. Asserted against the collector
        // rather than the generated code, because GenerateAllCode IS codegen and sets the flag itself.
        using var host = buildHost(typeof(SideEffectRootSample));

        // Resolving the code file collections is what compiles the handler graph; without it there is no
        // chain to ask.
        _ = host.Services.GetServices<ICodeFileCollection>().ToArray();

        var chain = host.Services.GetRequiredService<HandlerGraph>()
            .ChainFor(typeof(SideEffectRootMessage)).ShouldNotBeNull();

        DynamicCodeBuilder.WithinCodegenCommand.ShouldBeFalse();
        SideEffectAotRoots.Of(chain).ShouldBeEmpty();

        // ...and the same chain does answer while codegen is running, so the emptiness above is the guard
        // and not an empty walk.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            SideEffectAotRoots.Of(chain).ShouldBe([typeof(IDerivedSideEffect), typeof(IRootedSideEffect)]);
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static string generateAllCode(Type handlerType)
    {
        using var host = buildHost(handlerType);

        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        return builder.GenerateAllCode();
    }

    // One handler per host, deliberately: the rooting block is emitted per application, and the "not
    // rooted" assertions above would be false the moment another handler in the same host legitimately
    // returned the type in question.
    private static IHost buildHost(Type handlerType)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(handlerType);
            })
            .Build();
    }
}

public record SideEffectRootMessage;

/// <summary>
///     Declares <c>Execute</c>, so this is the interface <c>findMethod</c> finally finds it on.
/// </summary>
public interface IRootedSideEffect : ISideEffect
{
    void Execute();
}

/// <summary>
///     The declared return type, which declares nothing — the shape of Marten's <c>IStartStream</c>.
/// </summary>
public interface IDerivedSideEffect : IRootedSideEffect;

public class DerivedSideEffect : IDerivedSideEffect
{
    public void Execute()
    {
    }
}

// [WolverineIgnore] because conventional discovery in this assembly would otherwise hand this handler to
// every other host here, and a side effect applies to any chain that returns one.
[WolverineIgnore]
public static class SideEffectRootSample
{
    public static IDerivedSideEffect Handle(SideEffectRootMessage message)
    {
        return new DerivedSideEffect();
    }
}

public record SideEffectMarkerMessage;

/// <summary>
///     GH-4840. One more member-less marker on top of <see cref="IDerivedSideEffect" />, so that there is
///     an interface BETWEEN the return type and the one declaring <c>Execute</c>.
/// </summary>
public interface IDeeperSideEffect : IDerivedSideEffect;

public class DeeperSideEffect : IDeeperSideEffect
{
    public void Execute()
    {
    }
}

[WolverineIgnore]
public static class SideEffectMarkerSample
{
    public static IDeeperSideEffect Handle(SideEffectMarkerMessage message)
    {
        return new DeeperSideEffect();
    }
}

public record SideEffectOutsideMessage;

/// <summary>
///     GH-4840. Declares <c>Execute</c> and is NOT an <see cref="ISideEffect" />.
/// </summary>
public interface IExecutesOutsideWolverine
{
    void Execute();
}

/// <summary>
///     GH-4840. A side effect whose only <c>Execute</c> is the one it inherits from outside the
///     <see cref="ISideEffect" /> hierarchy.
/// </summary>
public interface IOutsideDeclaredSideEffect : ISideEffect, IExecutesOutsideWolverine;

public class OutsideDeclaredSideEffect : IOutsideDeclaredSideEffect
{
    public void Execute()
    {
    }
}

[WolverineIgnore]
public static class SideEffectOutsideSample
{
    public static IOutsideDeclaredSideEffect Handle(SideEffectOutsideMessage message)
    {
        return new OutsideDeclaredSideEffect();
    }
}
