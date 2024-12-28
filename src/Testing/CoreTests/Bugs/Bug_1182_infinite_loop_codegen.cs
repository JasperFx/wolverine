using System.Diagnostics;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_1182_infinite_loop_codegen
{
    [Fact]
    public async Task do_not_go_into_infinite_loop()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InfiniteCommandHandlingThing));
            }).StartAsync();
        
        var collections = host.Services.GetServices<ICodeFileCollection>().ToArray();

        var builder = new DynamicCodeBuilder(host.Services, collections)
        {
            ServiceVariableSource = host.Services.GetService<IServiceVariableSource>()
        };

        var ex = Should.Throw<CodeGenerationException>(() => builder.GenerateAllCode());
        ex.InnerException.InnerException.ShouldBeOfType<ArgumentOutOfRangeException>();

    }
}

public static class InfiniteCommandHandlingThing
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnException<Exception>().Requeue(int.MaxValue);
    }

    public static void Handle(InfiniteCommand command)
    {
    }
}

public record InfiniteCommand;