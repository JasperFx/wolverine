using CoreTests.Configuration;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace CoreTests;

public class BootstrappingSamples
{
    public static async Task AppWithHandlerPolicy()
    {
        #region sample_AppWithHandlerPolicy

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.Policies.Add<WrapWithSimple>(); }).StartAsync();

        #endregion
    }
}

#region sample_WrapWithSimple

public class WrapWithSimple : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains) chain.Middleware.Add(new SimpleWrapper());
    }
}

#endregion