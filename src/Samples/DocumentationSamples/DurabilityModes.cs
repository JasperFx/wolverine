using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;

namespace DocumentationSamples;

public class DurabilityModes
{
    public static async Task bootstrap_for_serverless()
    {
        #region sample_configuring_the_serverless_mode
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten("some connection string")

                    // This adds quite a bit of middleware for
                    // Marten
                    .IntegrateWithWolverine();

                // You want this maybe!
                opts.Policies.AutoApplyTransactions();


                // But wait! Optimize Wolverine for usage within Serverless
                // and turn off the heavy duty, background processes
                // for the transactional inbox/outbox
                opts.Durability.Mode = DurabilityMode.Serverless;
            }).StartAsync();

        #endregion
    }

    public static async Task bootstrap_for_mediator()
    {
        #region sample_configuring_the_mediator_mode
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Other configuration...
                
                // But wait! Optimize Wolverine for usage as *only*
                // a mediator
                opts.Durability.Mode = DurabilityMode.MediatorOnly;
            }).StartAsync();

        #endregion
    }

    public static async Task bootstrap_for_solo()
    {
        #region sample_configuring_the_solo_mode
        var builder = Host.CreateApplicationBuilder();

        builder.UseWolverine(opts =>
        {
            opts.Services.AddMarten("some connection string")

                // This adds quite a bit of middleware for
                // Marten
                .IntegrateWithWolverine();

            // You want this maybe!
            opts.Policies.AutoApplyTransactions();


            if (builder.Environment.IsDevelopment())
            {
                // But wait! Optimize Wolverine for usage as
                // if there would never be more than one node running
                opts.Durability.Mode = DurabilityMode.Solo;
            }
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public static async Task bootstrap_for_serverless_with_outbox()
    {
        #region sample_configuring_the_serverless_with_outbox_mode
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten("some connection string")
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();

                // The same lean runtime as Serverless, but outgoing
                // messages still go through the transactional outbox
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

                // Optional. This is already 60 seconds under ServerlessWithOutbox
                opts.Durability.OutboxStaleTime = 1.Minutes();

                // Optional. Adds an index on the outgoing envelope table's
                // timestamp column for the durability pass to use
                opts.Durability.EnableOutgoingTimestampIndex = true;
            }).StartAsync();

        #endregion
    }

    public static async Task run_a_durability_pass(IHost host)
    {
        #region sample_running_a_durability_pass
        // Run one bounded pass of durability work across every message store
        var report = await host.RunDurabilityPassAsync(maxDuration: 30.Seconds());

        // If the pass ran out of time or hit the batch size, it tells you
        // so and you can just go around again
        while (report.HasMoreWork && !report.TimedOut)
        {
            report = await host.RunDurabilityPassAsync(maxDuration: 30.Seconds());
        }

        Console.WriteLine($"Sent {report.OutgoingSent} outgoing messages, released {report.ScheduledReleased} scheduled messages");
        #endregion
    }
}
