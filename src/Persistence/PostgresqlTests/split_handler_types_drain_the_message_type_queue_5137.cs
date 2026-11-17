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
using Wolverine.Postgresql;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;

namespace PostgresqlTests;

/// <summary>
/// GH-5137. Wolverine 7 gives every handler type of a message type with several handler types a local queue of its
/// own, so nothing listens on the message type's own local queue any more. Durable envelopes persisted to that
/// queue before the upgrade -- a message type that had one handler, or a saga sharing its message with a handler,
/// both used it -- still have to be handled, by every handler type, when the inbox recovers them.
/// </summary>
public class split_handler_types_drain_the_message_type_queue_5137 : PostgresqlContext
{
    [Fact]
    public async Task an_envelope_persisted_to_the_message_types_own_queue_reaches_every_handler_type()
    {
        DrainHandlers5137.Received.Clear();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<FirstDrainHandler5137>()
                    .IncludeType<SecondDrainHandler5137>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "drain_5137");
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        var message = new DrainMessage5137(Guid.NewGuid());
        var oldQueue = new Uri("local://" + typeof(DrainMessage5137).ToMessageTypeName().ToLowerInvariant());

        // What a 6.x node left behind: an envelope on the message type's own durable queue, owned by nobody
        var envelope = new Envelope(message)
        {
            Destination = oldQueue,
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            MessageType = typeof(DrainMessage5137).ToMessageTypeName(),
            ContentType = EnvelopeConstants.JsonContentType,
            Data = JsonSerializer.SerializeToUtf8Bytes(message)
        };

        var store = host.GetRuntime().Storage;
        await store.Inbox.StoreIncomingAsync(envelope);

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < 60.Seconds() && DrainHandlers5137.Count(message.Id) < 2)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        DrainHandlers5137.ReceivedBy(message.Id)
            .ShouldBe([typeof(FirstDrainHandler5137), typeof(SecondDrainHandler5137)], ignoreOrder: true);

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
    }
}

public record DrainMessage5137(Guid Id);

public static class DrainHandlers5137
{
    public static readonly List<(Type HandlerType, Guid Id)> Received = [];

    public static void Record(Type handlerType, Guid id)
    {
        lock (Received) Received.Add((handlerType, id));
    }

    public static int Count(Guid id)
    {
        lock (Received) return Received.Count(x => x.Id == id);
    }

    public static Type[] ReceivedBy(Guid id)
    {
        lock (Received) return Received.Where(x => x.Id == id).Select(x => x.HandlerType).ToArray();
    }
}

// Hidden from conventional discovery so they cannot leak into every other host in this assembly
[WolverineIgnore]
public class FirstDrainHandler5137
{
    public void Handle(DrainMessage5137 message) => DrainHandlers5137.Record(GetType(), message.Id);
}

[WolverineIgnore]
public class SecondDrainHandler5137
{
    public void Handle(DrainMessage5137 message) => DrainHandlers5137.Record(GetType(), message.Id);
}
