using System;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Events;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;


namespace MartenTests.Bugs;

public class Bug_2026_scheduled_messages_with_partitioning
{
    //[Fact] -- don't run this, but this was used to fix GH-2026 w/ some manual testing
    public async Task send_messages_with_delay()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                
                opts.Services.AddMarten(m =>
                {
                    m.DatabaseSchemaName = "gh2026";
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                }).IntegrateWithWolverine();

                opts.Durability.EnableInboxPartitioning = true;
                opts.Policies.LogMessageStarting(LogLevel.Information);
                opts.Policies.MessageExecutionLogLevel(LogLevel.Information);
                opts.Policies.MessageSuccessLogLevel(LogLevel.Information);

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                // use a local partitioned queue for the ReproduceBug message type
                opts.MessagePartitioning.UseInferredMessageGrouping()
                    .ByMessage<ReproduceBug>(x => x.Name)
                    .PublishToPartitionedLocalMessaging("repro", 8, topology =>
                    {
                        topology.Message<ReproduceBug>();
                    });
            }).StartAsync();

        await host.RebuildAllEnvelopeStorageAsync();

        await host.SendAsync(new TriggerReproduceBug("Foo"));


        await Task.Delay(2.Minutes());
    }
}

public sealed record ReproduceBug(string Name);

public record TriggerReproduceBug(string Name);

public static class ReproduceBugHandler
{
    public static void Handle(ReproduceBug command)
    {
        Console.WriteLine($"Reproducing bug for {command.Name}");
    }

    public static OutgoingMessages Handle(TriggerReproduceBug cmd)
    {
        var outgoingMessages = new OutgoingMessages();
        outgoingMessages.Delay(new ReproduceBug(cmd.Name), 30.Seconds());
        return outgoingMessages;   
    }
}