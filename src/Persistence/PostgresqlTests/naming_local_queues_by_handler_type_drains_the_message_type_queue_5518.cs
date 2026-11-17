using System.Diagnostics;
using System.Text.Json;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Logging;
using Wolverine.Postgresql;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests;

/// <summary>
/// GH-5518. With NameQueuesByHandlerType(), a message type with one handler type runs on the local queue named for its
/// handler type instead of the message type's queue. Durable envelopes persisted to the message type's queue before
/// the option was turned on still have to be handled when the inbox recovers them.
/// </summary>
public class naming_local_queues_by_handler_type_drains_the_message_type_queue_5518 : PostgresqlContext
{
    [Fact]
    public async Task an_envelope_persisted_to_the_message_types_own_queue_is_still_handled()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<PackOrderHandler5518>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "drain_5518");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.UseDurableLocalQueues();
                opts.Policies.ConfigureConventionalLocalRouting().NameQueuesByHandlerType();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        // New messages go to the handler type's queue
        var handlerTypeQueue = new Uri("local://" + typeof(PackOrderHandler5518).FullNameInCode().ToLowerInvariant());
        host.GetRuntime().RoutingFor(typeof(PackOrder5518)).Routes.OfType<MessageRoute>().Select(x => x.Uri)
            .ShouldBe([handlerTypeQueue]);

        var message = new PackOrder5518(Guid.NewGuid());
        var oldQueue = new Uri("local://" + typeof(PackOrder5518).ToMessageTypeName().ToLowerInvariant());

        // What the application left behind before the option: an envelope on the message type's own durable queue,
        // owned by nobody
        var envelope = new Envelope(message)
        {
            Destination = oldQueue,
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            MessageType = typeof(PackOrder5518).ToMessageTypeName(),
            ContentType = EnvelopeConstants.JsonContentType,
            Data = JsonSerializer.SerializeToUtf8Bytes(message)
        };

        var store = host.GetRuntime().Storage;
        await store.Inbox.StoreIncomingAsync(envelope);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < 60.Seconds() && PackOrderHandler5518.Count(message.Id) < 1)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        PackOrderHandler5518.Count(message.Id).ShouldBe(1);

        // And the recovered row is retired rather than recovered again on the next pass
        stopwatch.Restart();
        PersistedCounts counts;
        do
        {
            counts = await store.Admin.FetchCountsAsync();
            if (counts.Incoming == 0) break;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        } while (stopwatch.Elapsed < 15.Seconds());

        counts.Incoming.ShouldBe(0);
        PackOrderHandler5518.Count(message.Id).ShouldBe(1);
    }
}

public record PackOrder5518(Guid Id);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class PackOrderHandler5518
{
    private static readonly List<Guid> _handled = [];

    public void Handle(PackOrder5518 message)
    {
        lock (_handled) _handled.Add(message.Id);
    }

    public static int Count(Guid id)
    {
        lock (_handled) return _handled.Count(x => x == id);
    }
}
