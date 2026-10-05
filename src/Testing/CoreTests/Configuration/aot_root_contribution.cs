using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

// GH-4765: a frame built by closing an open generic reflectively can name its own closed type as a
// Native AOT root, which is the only way a type belonging to a persistence package gets rooted at all --
// AddAotRoots is called from the handler and HTTP registry code files, and neither can reference a
// Wolverine.Marten or Wolverine.EntityFrameworkCore type.
public class aot_root_contribution
{
    [Fact]
    public void frames_contribute_their_closed_type_to_the_rooting_block()
    {
        // The rooting block is only emitted while actually generating code, same guard as the
        // message-type scan in handler_manifest_message_types.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var code = generateAllCode(new ContributeAotRootPolicy());

            // The closed type the policy's frame was built from -- not the open generic, which cannot be
            // named in a [DynamicDependency] at all.
            code.ShouldContain(
                "typeof(global::CoreTests.Configuration.AotRootSampleFrame<CoreTests.Configuration.AotRootSampleMessage>)");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public void a_non_public_contributed_root_is_left_out_rather_than_breaking_the_generated_file()
    {
        // A typeof() in the generated file cannot name an internal type, so the filter drops it. The same
        // rule is why Applier<> had to become public in GH-4790 -- but a frame nobody made public must
        // not take the whole generated file down with it.
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            var code = generateAllCode(new ContributeInternalAotRootPolicy());

            code.ShouldNotContain(nameof(InternalAotRootFrame));

            // ...and the rest of the block is still there
            code.ShouldContain(nameof(AotRootSampleHandler));
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static string generateAllCode(IHandlerPolicy policy)
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AotRootSampleHandler));

                opts.Policies.Add(policy);
            })
            .Build();

        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();
        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        return builder.GenerateAllCode();
    }
}

public record AotRootSampleMessage;

public static class AotRootSampleHandler
{
    public static void Handle(AotRootSampleMessage message)
    {
    }
}

/// <summary>
///     Stands in for the real thing — <c>EnrollAndFetchSagaStorageFrame&lt;,&gt;</c>, closed over the
///     user's saga type by two different frame providers.
/// </summary>
public class AotRootSampleFrame<T> : Frame, IAotRootSource
{
    public AotRootSampleFrame() : base(false)
    {
    }

    public IEnumerable<Type> AotRoots()
    {
        yield return GetType();
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        Next?.GenerateCode(method, writer);
    }
}

internal class InternalAotRootFrame : Frame, IAotRootSource
{
    public InternalAotRootFrame() : base(false)
    {
    }

    public IEnumerable<Type> AotRoots()
    {
        yield return GetType();
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        Next?.GenerateCode(method, writer);
    }
}

public class ContributeAotRootPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // Closed reflectively on purpose: this is the shape the issue is about.
            var frame = (Frame)Activator.CreateInstance(
                typeof(AotRootSampleFrame<>).MakeGenericType(chain.MessageType))!;
            chain.Middleware.Add(frame);
        }
    }
}

public class ContributeInternalAotRootPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            chain.Middleware.Add(new InternalAotRootFrame());
        }
    }
}
