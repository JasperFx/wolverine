using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using TestMessages;
using Wolverine;

namespace DocumentationSamples;

public class EnqueueSamples
{

    #region sample_invoke_locally

    public static async Task invoke_locally(ICommandBus bus)
    {
        // Execute the message inline
        await bus.InvokeAsync(new Message1());
    }

    #endregion

    public static async Task configura_all_queues()
    {
        #region sample_configuring_local_queues

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Explicit configuration
                opts.LocalQueue("one")
                    .Sequential();

                opts.LocalQueue("two")
                    .MaximumParallelMessages(10)
                    .UseDurableInbox();

                // Apply configuration options to all local queues,
                // but explicit changes to specific local queues take precedence
                opts.Policies.AllLocalQueues(x => x.UseDurableInbox());
            }).StartAsync();

        #endregion
    }

    public static async Task configure_local_conventions()
    {
        #region sample_local_queue_conventions

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Out of the box, this uses a separate local queue
                // for each message based on the message type name
                opts.Policies.ConfigureConventionalLocalRouting()

                    // Or you can customize the usage of queues
                    // per message type
                    .Named(type => type.Namespace)

                    // Create an optional deny list of types to fall under
                    // this convention
                    .ExcludeTypes(type => type.IsInNamespace("MyApp.NotHere"))

                    // Create an optional allow list of message types
                    // to fall under this routing convention
                    .IncludeTypes(type => type.IsInNamespace("MyApp.Here"))

                    // Optionally configure the local queues
                    .CustomizeQueues((type, listener) => { listener.Sequential(); });
            }).StartAsync();

        #endregion
    }
}