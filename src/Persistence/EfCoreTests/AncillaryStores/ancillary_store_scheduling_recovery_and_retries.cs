using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Wolverine.Util;

namespace EfCoreTests.AncillaryStores;

// Exploratory follow-ups to GH-5731 / GH-5732, carrying the scenarios the Marten ancillary store tests cover
// (GH-2576 scheduled messages, GH-2318 / GH-2382 inbox ownership) over to EF Core DbContexts in three separate
// databases. Each one moves an inbox row through a path other than "published, then handled":
//
//   * a durable local message scheduled from one store for a handler in another, promoted by the scheduled-jobs
//     poller of the store that holds it
//   * an Incoming row that the durability agent recovers from one store for a handler in another
//   * a handler on another store that fails once and runs again
//
// In every case the row must end up Handled in the database that holds it, and the message must run once.

public record AncScheduleFromMain(Guid Id, StoreChoice Target);

public record AncScheduleFromA(Guid Id, StoreChoice Target);

public record AncScheduleFromB(Guid Id, StoreChoice Target);

public record AncFlakyKickOff(Guid Id);

public record AncFlakyWorkInB(Guid Id);

// The publisher's transaction holds the scheduled row
public static class AncSchedulingHandlers
{
    public static object Handle(AncScheduleFromMain command, AncMainDbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "scheduled-kick" });
        return AncWork.For(command.Id, command.Target).DelayedFor(1.Seconds());
    }

    public static object Handle(AncScheduleFromA command, AncADbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "scheduled-kick" });
        return AncWork.For(command.Id, command.Target).DelayedFor(1.Seconds());
    }

    public static object Handle(AncScheduleFromB command, AncBDbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "scheduled-kick" });
        return AncWork.For(command.Id, command.Target).DelayedFor(1.Seconds());
    }
}

public static class AncFlakyWorkHandler
{
    public static readonly HashSet<Guid> FailedOnce = new();

    public static void Handle(AncFlakyWorkInB message, AncBDbContext db)
    {
        lock (FailedOnce)
        {
            if (FailedOnce.Add(message.Id)) throw new TimeoutException("Fails the first time only");
        }

        db.Rows.Add(new AncillaryRow { Id = message.Id, Step = "flaky" });
        AncillaryStoreHarness.Executed(message.Id, "flaky");
    }
}

public static class AncFlakyKickOffHandler
{
    public static AncFlakyWorkInB Handle(AncFlakyKickOff command, AncADbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "flaky-kick" });
        return new AncFlakyWorkInB(command.Id);
    }
}

public class ancillary_store_scheduling_recovery_and_retries
{
    private static Task<IHost> startHostAsync(params Type[] handlers)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = 100.Milliseconds();
                opts.Durability.ScheduledJobPollingTime = 500.Milliseconds();

                var discovery = opts.Discovery.DisableConventionalDiscovery();
                foreach (var handler in handlers) discovery.IncludeType(handler);

                AncillaryStoreHarness.ConfigureStores(opts);

                opts.PublishMessage<AncWorkInMain>().ToLocalQueue("anc-work").UseDurableInbox();
                opts.PublishMessage<AncWorkInA>().ToLocalQueue("anc-work").UseDurableInbox();
                opts.PublishMessage<AncWorkInB>().ToLocalQueue("anc-work").UseDurableInbox();
                opts.PublishMessage<AncFlakyWorkInB>().ToLocalQueue("anc-flaky").UseDurableInbox();

                opts.OnException<TimeoutException>().RetryOnce();
            }).StartAsync();
    }

    public static IEnumerable<object[]> StorePairs()
    {
        foreach (var from in Enum.GetValues<StoreChoice>())
        foreach (var to in Enum.GetValues<StoreChoice>())
        {
            yield return [from, to];
        }
    }

    private static async Task assertHandledOnceAndNothingStrandedAsync(IHost host, Guid id, string step,
        params Type[] messageTypes)
    {
        await AncillaryStoreHarness.WaitForAsync(
            () => Task.FromResult(AncillaryStoreHarness.ExecutionsOf(id, step) > 0), 30.Seconds());
        AncillaryStoreHarness.ExecutionsOf(id, step).ShouldBe(1, "the message never ran");

        var stranded = new List<string>();
        await AncillaryStoreHarness.WaitForAsync(async () =>
        {
            stranded.Clear();
            foreach (var messageType in messageTypes)
            {
                stranded.AddRange(await host.StrandedIncomingAsync(messageType));
                stranded.AddRange(await scheduledAsync(host, messageType));
            }

            return stranded.Count == 0;
        }, 10.Seconds());

        stranded.ShouldBeEmpty();
    }

    private static async Task<string[]> scheduledAsync(IHost host, Type messageType)
    {
        var list = new List<string>();
        foreach (var store in Enum.GetValues<StoreChoice>())
        {
            var incoming = await host.StoreFor(store).Admin.AllIncomingAsync();
            list.AddRange(incoming
                .Where(x => x.MessageType == messageType.ToMessageTypeName() && x.Status == EnvelopeStatus.Scheduled)
                .Select(x => $"{store}: still Scheduled"));
        }

        return list.ToArray();
    }

    private static async Task restartAndAssertNoReplayAsync(Guid id, string step, params Type[] handlers)
    {
        using var host = await startHostAsync(handlers);
        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
        AncillaryStoreHarness.ExecutionsOf(id, step).ShouldBe(1, "a handled message was replayed after a restart");
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [MemberData(nameof(StorePairs))]
    public async Task scheduled_local_message_across_stores(StoreChoice publisher, StoreChoice target)
    {
        await AncillaryStoreHarness.EnsureDatabasesAsync();
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        Type[] handlers = [typeof(AncSchedulingHandlers), typeof(AncWorkHandlers), typeof(AncFinishedHandler)];
        var workType = AncWork.For(id, target).GetType();

        using (var host = await startHostAsync(handlers))
        {
            await host.ResetResourceState(token);

            object kickOff = publisher switch
            {
                StoreChoice.Main => new AncScheduleFromMain(id, target),
                StoreChoice.A => new AncScheduleFromA(id, target),
                _ => new AncScheduleFromB(id, target)
            };

            await host.MessageBus().InvokeAsync(kickOff, token);

            await assertHandledOnceAndNothingStrandedAsync(host, id, "work", workType);
            (await AncillaryStoreHarness.RowExistsAsync(target, id, "work")).ShouldBeTrue();

            await host.StopAsync(token);
        }

        await restartAndAssertNoReplayAsync(id, "work", handlers);
    }

    [Theory]
    [MemberData(nameof(StorePairs))]
    public async Task incoming_row_recovered_from_one_store_for_a_handler_on_another(StoreChoice holder,
        StoreChoice target)
    {
        await AncillaryStoreHarness.EnsureDatabasesAsync();
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        Type[] handlers = [typeof(AncWorkHandlers), typeof(AncFinishedHandler)];

        using (var host = await startHostAsync(handlers))
        {
            await host.ResetResourceState(token);

            // Left by a process that died, or released by one that stopped: Incoming and owned by any node
            var message = AncWork.For(id, target);
            var envelope = new Envelope(message)
            {
                Id = Guid.NewGuid(),
                Destination = new Uri("local://anc-work"),
                Status = EnvelopeStatus.Incoming,
                OwnerId = 0,
                MessageType = message.GetType().ToMessageTypeName(),
                ContentType = "application/json",
                Data = host.GetRuntime().Options.DefaultSerializer.WriteMessage(message)
            };

            await host.StoreFor(holder).Inbox.StoreIncomingAsync(envelope);

            await assertHandledOnceAndNothingStrandedAsync(host, id, "work", message.GetType());
            (await AncillaryStoreHarness.RowExistsAsync(target, id, "work")).ShouldBeTrue();

            await host.StopAsync(token);
        }

        await restartAndAssertNoReplayAsync(id, "work", handlers);
    }

    [Fact]
    public async Task handler_on_another_store_that_fails_once_then_succeeds()
    {
        await AncillaryStoreHarness.EnsureDatabasesAsync();
        var token = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid();
        Type[] handlers = [typeof(AncFlakyKickOffHandler), typeof(AncFlakyWorkHandler)];

        using (var host = await startHostAsync(handlers))
        {
            await host.ResetResourceState(token);

            // Published from A's transaction, so the inbox row is in A, handled against B
            await host.MessageBus().InvokeAsync(new AncFlakyKickOff(id), token);

            await assertHandledOnceAndNothingStrandedAsync(host, id, "flaky", typeof(AncFlakyWorkInB));
            (await AncillaryStoreHarness.RowExistsAsync(StoreChoice.B, id, "flaky")).ShouldBeTrue();

            await host.StopAsync(token);
        }

        await restartAndAssertNoReplayAsync(id, "flaky", handlers);
    }
}
