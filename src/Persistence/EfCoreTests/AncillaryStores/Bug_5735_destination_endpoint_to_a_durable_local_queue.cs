using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;
using Wolverine.Util;

namespace EfCoreTests.AncillaryStores;

// GH-5735. EndpointFor("queue").SendAsync() to a durable local queue, inside an EF Core outbox on a DbContext
// enrolled in an ancillary store, wrote the inbox row as Outgoing. Recovery only takes Incoming rows, so a process
// that died before the handler finished lost the message. Publishing the same message through routing wrote Incoming.

public record AncHeldWork(Guid Id);

public static class AncHeldWorkHandler
{
    public static TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static async Task Handle(AncHeldWork message)
    {
        Started.TrySetResult();
        await Release.Task;
        AncillaryStoreHarness.Executed(message.Id, "held");
    }
}

public class Bug_5735_destination_endpoint_to_a_durable_local_queue
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task the_inbox_row_is_incoming_and_owned_by_this_node(bool explicitEndpoint)
    {
        await AncillaryStoreHarness.EnsureDatabasesAsync();
        var token = TestContext.Current.CancellationToken;

        AncHeldWorkHandler.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AncHeldWorkHandler.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AncHeldWorkHandler));

                AncillaryStoreHarness.ConfigureStores(opts);

                opts.LocalQueue("anc-b-inbox").UseDurableInbox();
                opts.PublishMessage<AncHeldWork>().ToLocalQueue("anc-b-inbox");
            }).StartAsync(token);

        await host.ResetResourceState(token);

        var id = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IDbContextOutbox<AncBDbContext>>();
            if (explicitEndpoint)
            {
                await outbox.EndpointFor("anc-b-inbox").SendAsync(new AncHeldWork(id));
            }
            else
            {
                await outbox.PublishAsync(new AncHeldWork(id));
            }

            await outbox.SaveChangesAndFlushMessagesAsync(token);
        }

        await AncHeldWorkHandler.Started.Task.WaitAsync(10.Seconds(), token);

        var row = (await host.StoreFor(StoreChoice.B).Admin.AllIncomingAsync())
            .Single(x => x.MessageType == typeof(AncHeldWork).ToMessageTypeName());
        row.Status.ShouldBe(EnvelopeStatus.Incoming, "an Outgoing row in the inbox is never recovered");
        row.OwnerId.ShouldNotBe(TransportConstants.AnyNode, "a row any node can claim would run twice");

        AncHeldWorkHandler.Release.SetResult();

        await AncillaryStoreHarness.WaitForAsync(async () => (await host.StrandedIncomingAsync(typeof(AncHeldWork))).Length == 0,
            10.Seconds());
        (await host.StrandedIncomingAsync(typeof(AncHeldWork))).ShouldBeEmpty();
        AncillaryStoreHarness.ExecutionsOf(id, "held").ShouldBe(1);

        await host.StopAsync(token);
    }
}
