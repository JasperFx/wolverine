using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.Transports.Tcp;

namespace EfCoreTests.AncillaryStores;

// GH-5731 / GH-5732. Every combination of the store a message is published from, the store its handler runs
// against, and how it gets there (a durable local queue, TCP with the durable inbox and outbox, a SQL Server queue),
// over three separate databases. For each one:
//
//   * the receiving handler runs exactly once and its write commits in its own database
//   * no inbox row for the message is left Incoming in ANY of the databases, and no outgoing row is left behind
//   * a restart replays nothing
//
// GH-5731 was the durable local queue from A to B: the inbox row was written in A by A's transaction, the receiver
// relabelled it as B's, and B's transaction then "marked" it on B's connection. GH-5732 was the SQL Server queue:
// the handler's transaction resolved the main store instead of B's, believed it held the row, and did the same.

public enum AncTransport
{
    DurableLocalQueue,
    Tcp,
    SqlServerQueue
}

public record AncKickOffFromMain(Guid Id, StoreChoice Target);

public record AncKickOffFromA(Guid Id, StoreChoice Target);

public record AncKickOffFromB(Guid Id, StoreChoice Target);

public record AncWorkInMain(Guid Id);

public record AncWorkInA(Guid Id);

public record AncWorkInB(Guid Id);

// Cascaded by every work handler over TCP with the durable outbox, so each case also proves the outgoing row its
// transaction wrote is deleted from the database it was written to once it is sent
public record AncFinished(Guid Id);

public static class AncFinishedHandler
{
    public static void Handle(AncFinished message)
    {
        AncillaryStoreHarness.Executed(message.Id, "finished");
    }
}

public static class AncWork
{
    public static object For(Guid id, StoreChoice target)
    {
        return target switch
        {
            StoreChoice.Main => new AncWorkInMain(id),
            StoreChoice.A => new AncWorkInA(id),
            _ => new AncWorkInB(id)
        };
    }
}

public static class AncPublisherHandlers
{
    public static OutgoingMessages Handle(AncKickOffFromMain command, AncMainDbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "kick-main" });
        return [AncWork.For(command.Id, command.Target)];
    }

    public static OutgoingMessages Handle(AncKickOffFromA command, AncADbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "kick-a" });
        return [AncWork.For(command.Id, command.Target)];
    }

    public static OutgoingMessages Handle(AncKickOffFromB command, AncBDbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = command.Id, Step = "kick-b" });
        return [AncWork.For(command.Id, command.Target)];
    }
}

public static class AncWorkHandlers
{
    public static AncFinished Handle(AncWorkInMain message, AncMainDbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = message.Id, Step = "work" });
        AncillaryStoreHarness.Executed(message.Id, "work");
        return new AncFinished(message.Id);
    }

    public static AncFinished Handle(AncWorkInA message, AncADbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = message.Id, Step = "work" });
        AncillaryStoreHarness.Executed(message.Id, "work");
        return new AncFinished(message.Id);
    }

    public static AncFinished Handle(AncWorkInB message, AncBDbContext db)
    {
        db.Rows.Add(new AncillaryRow { Id = message.Id, Step = "work" });
        AncillaryStoreHarness.Executed(message.Id, "work");
        return new AncFinished(message.Id);
    }
}

public class ancillary_store_messaging_permutations
{
    private const int TcpPort = 2_57_31;
    private const int FinishedPort = 2_57_32;

    public static IEnumerable<object[]> Permutations()
    {
        foreach (var transport in Enum.GetValues<AncTransport>())
        foreach (var publisher in Enum.GetValues<StoreChoice>())
        foreach (var target in Enum.GetValues<StoreChoice>())
        {
            yield return [publisher, target, transport, false];
        }

        // The Wolverine-mapped DbContexts write their envelope rows through the change tracker instead of ADO
        // commands. The cross-database cases are the ones worth repeating
        foreach (var transport in Enum.GetValues<AncTransport>())
        {
            yield return [StoreChoice.A, StoreChoice.B, transport, true];
            yield return [StoreChoice.Main, StoreChoice.B, transport, true];
            yield return [StoreChoice.A, StoreChoice.Main, transport, true];
        }
    }

    private static Task<IHost> startHostAsync(StoreChoice target, AncTransport transport, bool wolverineMapped)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AncPublisherHandlers))
                    .IncludeType(typeof(AncWorkHandlers))
                    .IncludeType(typeof(AncFinishedHandler));

                AncillaryStoreHarness.ConfigureStores(opts, wolverineMapped);

                opts.PublishMessage<AncFinished>().ToPort(FinishedPort).UseDurableOutbox();
                opts.ListenAtPort(FinishedPort).UseDurableInbox();

                var workType = AncWork.For(Guid.Empty, target).GetType();
                switch (transport)
                {
                    case AncTransport.DurableLocalQueue:
                        opts.PublishMessage(workType).ToLocalQueue("anc-work").UseDurableInbox();
                        break;

                    case AncTransport.Tcp:
                        opts.PublishMessage(workType).ToPort(TcpPort).UseDurableOutbox();
                        opts.ListenAtPort(TcpPort).UseDurableInbox();
                        break;

                    case AncTransport.SqlServerQueue:
                        opts.PublishMessage(workType).ToSqlServerQueue("anc_work");
                        opts.ListenToSqlServerQueue("anc_work");
                        break;
                }
            }).StartAsync();
    }

    private static object kickOff(Guid id, StoreChoice publisher, StoreChoice target)
    {
        return publisher switch
        {
            StoreChoice.Main => new AncKickOffFromMain(id, target),
            StoreChoice.A => new AncKickOffFromA(id, target),
            _ => new AncKickOffFromB(id, target)
        };
    }

    [Theory]
    [MemberData(nameof(Permutations))]
    public async Task handled_once_marked_handled_where_the_row_is_and_never_replayed(StoreChoice publisher,
        StoreChoice target, AncTransport transport, bool wolverineMapped)
    {
        await AncillaryStoreHarness.EnsureDatabasesAsync();

        var id = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var workType = AncWork.For(id, target).GetType();
        var kickOffType = kickOff(id, publisher, target).GetType();

        using (var host = await startHostAsync(target, transport, wolverineMapped))
        {
            await host.ResetResourceState(token);

            await host.MessageBus().InvokeAsync(kickOff(id, publisher, target), token);

            await AncillaryStoreHarness.WaitForAsync(
                () => Task.FromResult(AncillaryStoreHarness.ExecutionsOf(id, "finished") > 0), 30.Seconds());
            AncillaryStoreHarness.ExecutionsOf(id, "work").ShouldBe(1, "the receiving handler never ran");
            AncillaryStoreHarness.ExecutionsOf(id, "finished").ShouldBe(1, "the cascaded message never arrived");

            (await AncillaryStoreHarness.RowExistsAsync(target, id, "work"))
                .ShouldBeTrue("the receiving handler's write belongs in its own database");

            // Marking the envelope handled is asynchronous relative to the handler finishing
            string[] stranded = [];
            await AncillaryStoreHarness.WaitForAsync(async () =>
            {
                stranded =
                [
                    ..await host.StrandedIncomingAsync(workType), ..await host.StrandedOutgoingAsync(workType),
                    ..await host.StrandedIncomingAsync(kickOffType),
                    ..await host.StrandedIncomingAsync(typeof(AncFinished)),
                    ..await host.StrandedOutgoingAsync(typeof(AncFinished))
                ];
                return stranded.Length == 0;
            }, 10.Seconds());

            stranded.ShouldBeEmpty();

            await host.StopAsync(token);
        }

        using (var host = await startHostAsync(target, transport, wolverineMapped))
        {
            // Inbox recovery would replay a row left Incoming
            await Task.Delay(3.Seconds(), token);
            AncillaryStoreHarness.ExecutionsOf(id, "work").ShouldBe(1, "a handled message was replayed after a restart");
            AncillaryStoreHarness.ExecutionsOf(id, "finished").ShouldBe(1, "a sent message was sent again after a restart");
            (await host.StrandedIncomingAsync(workType)).ShouldBeEmpty();

            await host.StopAsync(token);
        }
    }
}
