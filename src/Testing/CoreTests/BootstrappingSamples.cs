using CoreTests.Configuration;
using JasperFx.CodeGeneration;
using Lamar;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace CoreTests;

public class BootstrappingSamples
{
    public static async Task AppWithHandlerPolicy()
    {
        #region sample_AppWithHandlerPolicy

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Handlers.AddPolicy<WrapWithSimple>(); }).StartAsync();

        #endregion
    }
}

#region sample_WrapWithSimple

public class WrapWithSimple : IHandlerPolicy
{
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        foreach (var chain in graph.Chains) chain.Middleware.Add(new SimpleWrapper());
    }
}

#endregion