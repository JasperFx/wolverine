using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Configuration;

public record ReuseCompiledMessage(string Name);

public class ReuseCompiledMessageHandler
{
    public static void Handle(ReuseCompiledMessage message)
    {
    }
}

// GH-5378 / jasperfx#1114
public class reusing_compiled_assemblies
{
    private static Task<IHost> startHost(bool reuse)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ReuseCompiledMessageHandler>();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                opts.CodeGeneration.ReuseCompiledAssemblies = reuse;
            }).StartAsync();
    }

    private static Type compiledHandlerType(IHost host)
    {
        var chain = host.GetRuntime().Handlers.ChainFor<ReuseCompiledMessage>()!;
        host.GetRuntime().Handlers.HandlerFor<ReuseCompiledMessage>();
        return chain.Handler!.GetType();
    }

    [Fact]
    public async Task two_hosts_share_the_compiled_handler_assembly_when_on()
    {
        using var one = await startHost(true);
        using var two = await startHost(true);

        compiledHandlerType(one).Assembly.ShouldBeSameAs(compiledHandlerType(two).Assembly);
    }

    [Fact]
    public async Task two_hosts_compile_separately_by_default()
    {
        using var one = await startHost(false);
        using var two = await startHost(false);

        compiledHandlerType(one).Assembly.ShouldNotBeSameAs(compiledHandlerType(two).Assembly);
    }
}
