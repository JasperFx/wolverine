using Fisher;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Fisher;
using Wolverine.Fisher.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Util;

namespace FisherTests;

// Regression test for GH-5045. A durable local message whose handler commits an ANCILLARY Fisher session.
//
// - Control: a discovered [FisherStore] handler. The GH-2669 inbox->store map covers it, so the envelope is
//   persisted in the ancillary file and marked handled there.
// - Suspect: the same work done by a pre-canned MessageHandler registered with AddMessageHandler (the shape
//   pre-generated handlers take). Its bare chain is not in HandlerGraph.AllChains(), so the map never covers
//   it and the envelope is persisted in the MAIN file -- while FlushOutgoingMessagesOnCommit, enlisted on the
//   ancillary session, issued the mark-handled UPDATE against the ancillary file and flipped Status = Handled,
//   so DurableReceiver skipped the real mark. The listener now leaves an envelope whose row lives in another
//   store to DurableReceiver.
public interface IReplayAncillaryStore : IDocumentStore;

public record DiscoveredReplayMessage(Guid Id);

public record RegisteredReplayMessage(Guid Id);

public record RegisteredNoSessionReplayMessage(Guid Id);

public class RegisteredNoSessionReplayHandler : MessageHandler
{
    public static int Count;

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Count);
        return Task.CompletedTask;
    }
}

public class ReplayDoc
{
    public Guid Id { get; set; }
}

[FisherStore(typeof(IReplayAncillaryStore))]
public static class DiscoveredReplayHandler
{
    public static int Count;

    public static void Handle(DiscoveredReplayMessage message, IDocumentSession session)
    {
        Interlocked.Increment(ref Count);
        session.Store(new ReplayDoc { Id = message.Id });
    }
}

public class RegisteredReplayHandler : MessageHandler
{
    public static int Count;

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Count);

        // The exact shape of a pre-generated handler on an ancillary store.
        var factory = (OutboxedSessionFactory)context.Runtime.Services
            .GetRequiredService<OutboxedSessionFactory<IReplayAncillaryStore>>();
        await using var session = factory.OpenSession(context);

        var message = (RegisteredReplayMessage)context.Envelope!.Message!;
        session.Store(new ReplayDoc { Id = message.Id });

        await session.SaveChangesAsync(cancellation);
        await context.FlushOutgoingMessagesAsync();
    }
}

public class Bug_5045_registered_handler_on_ancillary_session_marks_handled : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private FisherTestDatabase theMain = null!;
    private FisherTestDatabase theAncillary = null!;

    public Bug_5045_registered_handler_on_ancillary_session_marks_handled(ITestOutputHelper output)
    {
        _output = output;
    }

    public ValueTask InitializeAsync()
    {
        theMain = Servers.CreateDatabase("replay_main");
        theAncillary = Servers.CreateDatabase("replay_anc");
        DiscoveredReplayHandler.Count = 0;
        RegisteredReplayHandler.Count = 0;
        RegisteredNoSessionReplayHandler.Count = 0;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        theMain.Dispose();
        theAncillary.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task<IHost> startAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(DiscoveredReplayHandler));
                opts.AddMessageHandler(typeof(RegisteredReplayMessage), new RegisteredReplayHandler());
                opts.AddMessageHandler(typeof(RegisteredNoSessionReplayMessage), new RegisteredNoSessionReplayHandler());

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.LocalQueue("replay").UseDurableInbox();
                opts.PublishMessage<DiscoveredReplayMessage>().ToLocalQueue("replay");
                opts.PublishMessage<RegisteredReplayMessage>().ToLocalQueue("replay");
                opts.PublishMessage<RegisteredNoSessionReplayMessage>().ToLocalQueue("replay");

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theMain.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();

                opts.Services.AddFisherStore<IReplayAncillaryStore>(m =>
                    {
                        m.Connection(theAncillary.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    // Distinct prefix, so "the wrong file" is also "the wrong table name" and nothing aliases.
                    .IntegrateWithWolverine(x => x.SchemaName = "replay_anc");
            }).StartAsync();
    }

    private async Task<string> dumpAsync(IHost host, Guid id)
    {
        var runtime = host.GetRuntime();
        var anc = (await runtime.FindAncillaryStoreForMarkerType(typeof(IReplayAncillaryStore)).Admin.AllIncomingAsync())
            .ToList();
        var line = $"main: [{string.Join(", ", (await runtime.Storage.Admin.AllIncomingAsync()).Select(e => $"{e.MessageType}={e.Status}"))}] " +
                   $"ancillary: [{string.Join(", ", anc.Select(e => $"{e.MessageType}={e.Status}"))}]";
        _output.WriteLine(line);
        return line;
    }

    private static async Task<int> incomingCountAsync(IMessageStore store, string messageType)
    {
        return (await store.Admin.AllIncomingAsync())
            .Count(e => e.MessageType == messageType && e.Status == EnvelopeStatus.Incoming);
    }

    private async Task<(int main, int ancillary)> settleAsync(IHost host, string messageType)
    {
        var runtime = host.GetRuntime();
        var anc = runtime.FindAncillaryStoreForMarkerType(typeof(IReplayAncillaryStore));

        // mark-as-handled is asynchronous after the handler returns; give it ample time.
        (int, int) counts = default;
        for (var i = 0; i < 50; i++)
        {
            counts = (await incomingCountAsync(runtime.Storage, messageType), await incomingCountAsync(anc, messageType));
            if (counts is (0, 0)) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return counts;
    }

    [Fact]
    public async Task control_discovered_ancillary_handler_is_marked_handled_and_not_replayed()
    {
        var id = Guid.NewGuid();
        var typeName = typeof(DiscoveredReplayMessage).ToMessageTypeName();

        using (var host = await startAsync())
        {
            await host.TrackActivity().Timeout(30.Seconds()).SendMessageAndWaitAsync(new DiscoveredReplayMessage(id));
            DiscoveredReplayHandler.Count.ShouldBe(1);

            var (main, anc) = await settleAsync(host, typeName);
            await dumpAsync(host, id);
            main.ShouldBe(0);
            anc.ShouldBe(0);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var host = await startAsync())
        {
            await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        DiscoveredReplayHandler.Count.ShouldBe(1);
    }

    [Fact]
    public async Task registered_handler_on_ancillary_session_is_marked_handled_and_not_replayed()
    {
        var id = Guid.NewGuid();
        var typeName = typeof(RegisteredReplayMessage).ToMessageTypeName();
        int main, anc, afterRestartMain;

        using (var host = await startAsync())
        {
            await host.TrackActivity().Timeout(30.Seconds()).SendMessageAndWaitAsync(new RegisteredReplayMessage(id));
            RegisteredReplayHandler.Count.ShouldBe(1);

            (main, anc) = await settleAsync(host, typeName);
            await dumpAsync(host, id);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var host = await startAsync())
        {
            // Durability recovery replays a stranded Incoming row here.
            await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
            await dumpAsync(host, id);
            afterRestartMain = await incomingCountAsync(host.GetRuntime().Storage, typeName);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        _output.WriteLine($"handler invocations: {RegisteredReplayHandler.Count}; main Incoming after first run: {main}; after restart: {afterRestartMain}");

        this.ShouldSatisfyAllConditions(
            () => main.ShouldBe(0, "the envelope should not be left Incoming in the MAIN store"),
            () => anc.ShouldBe(0),
            () => RegisteredReplayHandler.Count.ShouldBe(1, "a handled message was replayed after a restart"));
    }

    // Isolates the cause: the same registered (bare-chain) handler, envelope in the same MAIN store, but with no
    // ancillary session -- so no FlushOutgoingMessagesOnCommit. DurableReceiver's own mark-as-handled then
    // runs against the envelope's store, and nothing is stranded.
    [Fact]
    public async Task control_registered_handler_without_a_session_is_marked_handled_and_not_replayed()
    {
        var id = Guid.NewGuid();
        var typeName = typeof(RegisteredNoSessionReplayMessage).ToMessageTypeName();

        using (var host = await startAsync())
        {
            await host.TrackActivity().Timeout(30.Seconds()).SendMessageAndWaitAsync(new RegisteredNoSessionReplayMessage(id));
            var (main, anc) = await settleAsync(host, typeName);
            await dumpAsync(host, id);
            main.ShouldBe(0);
            anc.ShouldBe(0);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        using (var host = await startAsync())
        {
            await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        RegisteredNoSessionReplayHandler.Count.ShouldBe(1);
    }
}
