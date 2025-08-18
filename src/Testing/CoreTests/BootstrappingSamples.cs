using CoreTests.Configuration;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
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

    public static async Task DisableRemoteInvocation()
    {
        #region sample_disabling_remote_invocation

        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // This will disallow Wolverine from making remote calls
                // through IMessageBus.InvokeAsync() or InvokeAsync<T>()
                // Instead, Wolverine will throw an InvalidOperationException
                opts.EnableRemoteInvocation = false;
            }).StartAsync();

        #endregion
    }

    public static async Task enable_dead_letter_queue_expiration()
    {
        #region sample_enabling_dead_letter_queue_expiration

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {

                // This is required
                opts.Durability.DeadLetterQueueExpirationEnabled = true;

                // Default is 10 days. This is the retention period
                opts.Durability.DeadLetterQueueExpiration = 3.Days();

            }).StartAsync();

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