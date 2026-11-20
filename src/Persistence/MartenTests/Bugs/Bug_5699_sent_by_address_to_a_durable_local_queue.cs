using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MartenTests.Bugs;

/// <summary>
/// GH-5699. A message sent BY ADDRESS to a durable local queue inside a transaction -- <c>EndpointFor(...).SendAsync()</c>,
/// or a saga's <c>Fault&lt;T&gt;</c> going to its reply address -- never went through the local message route that marks a
/// local envelope Incoming. Its inbox row was written as <c>status = 'Outgoing'</c> with no owner, and recovery only picks
/// up Incoming rows, so after a crash between the commit and the hand-off to the local queue the message was never
/// delivered.
/// </summary>
public class Bug_5699_sent_by_address_to_a_durable_local_queue : IAsyncLifetime
{
    private const string SchemaName = "gh5699";
    private const string QueueName = "gh5699-durable";

    private static readonly Uri QueueUri = new("local://" + QueueName);
    private static readonly TimeSpan DeliveryTimeout = 30.Seconds();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await startHostAsync("first");
        await theHost.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static Task<IHost> startHostAsync(string name)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // A restart recovers the inbox on the durability agent's next pass: look every second
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AddressedToADurableQueueHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SchemaName;
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = SchemaName);

                // Nothing routes AddressedToADurableQueue here: it only ever arrives by this address
                opts.LocalQueue(QueueName).UseDurableInbox();

                opts.Services.AddSingleton(new ReceivingHost(name));
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);
    }

    /// <summary>
    /// Send by address through an enrolled Marten session and commit. With <paramref name="crashBeforeTheFlush" />, the
    /// listener that hands the committed envelopes to their queues is taken off first: the commit happens, the hand-off
    /// never does, as if the process died in between.
    /// </summary>
    private async Task sendByAddressInATransactionAsync(Guid id, bool crashBeforeTheFlush)
    {
        await using var scope = theHost.Services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IMartenOutbox>();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        outbox.Enroll(session);

        if (crashBeforeTheFlush)
        {
            session.Listeners.RemoveAll(x => x is FlushOutgoingMessagesOnCommit);
        }

        session.Store(new AddressedToADurableQueueDoc { Id = id });
        await outbox.EndpointFor(QueueUri).SendAsync(new AddressedToADurableQueue(id));

        await session.SaveChangesAsync(Token);
    }

    [Fact]
    public async Task the_inbox_row_is_incoming_and_owned_by_this_node()
    {
        var id = Guid.NewGuid();

        await sendByAddressInATransactionAsync(id, crashBeforeTheFlush: true);

        var runtime = theHost.GetRuntime();
        var rows = (await runtime.Storage.Admin.AllIncomingAsync())
            .Where(x => x.MessageType == typeof(AddressedToADurableQueue).ToMessageTypeName())
            .ToList();

        var row = rows.ShouldHaveSingleItem();
        row.Status.ShouldBe(EnvelopeStatus.Incoming, "recovery only ever picks up Incoming rows");
        row.OwnerId.ShouldBe(runtime.DurabilitySettings.AssignedNodeNumber);
    }

    [Fact]
    public async Task it_is_delivered_on_commit()
    {
        var id = Guid.NewGuid();

        await sendByAddressInATransactionAsync(id, crashBeforeTheFlush: false);

        (await AddressedToADurableQueueHandler.WaitForAsync(id, DeliveryTimeout)).ShouldBe("first");
    }

    [Fact]
    public async Task it_is_delivered_by_a_new_host_after_a_crash_between_the_commit_and_the_hand_off()
    {
        var id = Guid.NewGuid();

        await sendByAddressInATransactionAsync(id, crashBeforeTheFlush: true);

        // The first host stops with the message still in its inbox; a new one is started against the same database, and
        // only has what the first left behind
        await theHost.StopAsync(Token);
        theHost.Dispose();

        theHost = await startHostAsync("second");

        (await AddressedToADurableQueueHandler.WaitForAsync(id, DeliveryTimeout)).ShouldBe("second");
    }
}

public record AddressedToADurableQueue(Guid Id);

public class AddressedToADurableQueueDoc
{
    public Guid Id { get; set; }
}

public record ReceivingHost(string Name);

public static class AddressedToADurableQueueHandler
{
    private static readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _deliveries = new();

    private static TaskCompletionSource<string> deliveryOf(Guid id)
        => _deliveries.GetOrAdd(id, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

    public static void Handle(AddressedToADurableQueue message, ReceivingHost host)
    {
        deliveryOf(message.Id).TrySetResult(host.Name);
    }

    /// <summary>The name of the host that handled the message</summary>
    public static Task<string> WaitForAsync(Guid id, TimeSpan timeout) => deliveryOf(id).Task.WaitAsync(timeout);
}
