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
            var code = generateAllCode();

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
    public void does_not_bother_rooting_isideeffect_itself()
    {
        // ISideEffect declares no members at all, so an entry for it would be pure noise in a block that
        // every AOT application carries and the codegen drift gate byte-compares.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            generateAllCode().ShouldNotContain("typeof(global::Wolverine.ISideEffect)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void collects_nothing_outside_codegen()
    {
        // BuildFiles is enumerated during TypeLoadMode.Static ATTACH as well, where no rooting block is
        // emitted. Walking GetInterfaces() over every side effect there would be reflection a native image
        // pays for at startup to build a list that is then thrown away. Asserted against the collector
        // rather than the generated code, because GenerateAllCode IS codegen and sets the flag itself.
        using var host = buildHost();

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

    private static string generateAllCode()
    {
        using var host = buildHost();

        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        return builder.GenerateAllCode();
    }

    private static IHost buildHost()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SideEffectRootSample));
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
