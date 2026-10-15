using IntegrationTests;
using JasperFx.Resources;
using Polecat;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Polecat;
using Wolverine.Polecat.Publishing;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Wolverine.Util;

namespace PolecatTests.Bugs;

// Regression test for GH-5045. A durable local message whose handler commits an ANCILLARY Polecat session.
//
// - Control: a discovered [PolecatStore] handler. The GH-2669 inbox->store map covers it, so the envelope is
//   persisted in the ancillary schema and marked handled there.
// - Suspect: the same work done by a pre-canned MessageHandler registered with AddMessageHandler (the shape
//   pre-generated handlers take). Its bare chain is not in HandlerGraph.AllChains(), so the map never covers
//   it and the envelope is persisted in the MAIN schema -- while FlushOutgoingMessagesOnCommit, enlisted on the
//   ancillary session, issued the mark-handled UPDATE against the ancillary schema and flipped Status = Handled,
//   so DurableReceiver skipped the real mark. The listener now leaves an envelope whose row lives in another
//   store to DurableReceiver.
public interface IPolecatReplayAncillaryStore : IDocumentStore;

public record PolecatDiscoveredReplayMessage(Guid Id);

public record PolecatRegisteredReplayMessage(Guid Id);

public record PolecatRegisteredNoSessionReplayMessage(Guid Id);

public class PolecatRegisteredNoSessionReplayHandler : MessageHandler
{
    public static int Count;

    public override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Count);
        return Task.CompletedTask;
    }
}

public class PolecatReplayDoc
{
    public Guid Id { get; set; }
}

[PolecatStore(typeof(IPolecatReplayAncillaryStore))]
public static class PolecatDiscoveredReplayHandler
{
    public static int Count;

    public static void Handle(PolecatDiscoveredReplayMessage message, IDocumentSession session)
    {
        Interlocked.Increment(ref Count);
        session.Store(new PolecatReplayDoc { Id = message.Id });
    }
}

public class PolecatRegisteredReplayHandler : MessageHandler
{
    public static int Count;

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Count);

        // The exact shape of a pre-generated handler on an ancillary store.
        var factory = (OutboxedSessionFactory)context.Runtime.Services
            .GetRequiredService<OutboxedSessionFactory<IPolecatReplayAncillaryStore>>();
        await using var session = factory.OpenSession(context);

        var message = (PolecatRegisteredReplayMessage)context.Envelope!.Message!;
        session.Store(new PolecatReplayDoc { Id = message.Id });

        await session.SaveChangesAsync(cancellation);
        await context.FlushOutgoingMessagesAsync();
    }
}

public class Bug_5045_registered_handler_on_ancillary_polecat_session_marks_handled : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private const string MainSchema = "bug5045_main";
    private const string AncillarySchema = "bug5045_anc";

    public Bug_5045_registered_handler_on_ancillary_polecat_session_marks_handled(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        // Unlike Fisher's throwaway files, these schemas outlive a run, so clear what an earlier one left
        using (var host = await startAsync())
        {
            await host.ResetResourceState(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        PolecatDiscoveredReplayHandler.Count = 0;
        PolecatRegisteredReplayHandler.Count = 0;
        PolecatRegisteredNoSessionReplayHandler.Count = 0;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private Task<IHost> startAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PolecatDiscoveredReplayHandler));
                opts.AddMessageHandler(typeof(PolecatRegisteredReplayMessage), new PolecatRegisteredReplayHandler());
                opts.AddMessageHandler(typeof(PolecatRegisteredNoSessionReplayMessage), new PolecatRegisteredNoSessionReplayHandler());

                opts.Durability.Mode = DurabilityMode.Solo;

                opts.LocalQueue("replay").UseDurableInbox();
                opts.PublishMessage<PolecatDiscoveredReplayMessage>().ToLocalQueue("replay");
                opts.PublishMessage<PolecatRegisteredReplayMessage>().ToLocalQueue("replay");
                opts.PublishMessage<PolecatRegisteredNoSessionReplayMessage>().ToLocalQueue("replay");

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = MainSchema;
                    })
                    .IntegrateWithWolverine();

                opts.Services.AddPolecatStore<IPolecatReplayAncillaryStore>(m =>
                    {
                        m.Connection(Servers.SqlServerConnectionString);
                        m.DatabaseSchemaName = AncillarySchema;
                    })
                    // A distinct schema, so "the wrong store" is also "the wrong table" and nothing aliases
                    .IntegrateWithWolverine(x => x.SchemaName = AncillarySchema);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    private async Task<string> dumpAsync(IHost host, Guid id)
    {
        var runtime = host.GetRuntime();
        var anc = (await runtime.FindAncillaryStoreForMarkerType(typeof(IPolecatReplayAncillaryStore)).Admin.AllIncomingAsync())
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
        var anc = runtime.FindAncillaryStoreForMarkerType(typeof(IPolecatReplayAncillaryStore));

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
        var typeName = typeof(PolecatDiscoveredReplayMessage).ToMessageTypeName();

        using (var host = await startAsync())
        {
            await host.TrackActivity().Timeout(30.Seconds()).SendMessageAndWaitAsync(new PolecatDiscoveredReplayMessage(id));
            PolecatDiscoveredReplayHandler.Count.ShouldBe(1);

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

        PolecatDiscoveredReplayHandler.Count.ShouldBe(1);
    }

    [Fact]
    public async Task registered_handler_on_ancillary_session_is_marked_handled_and_not_replayed()
    {
        var id = Guid.NewGuid();
        var typeName = typeof(PolecatRegisteredReplayMessage).ToMessageTypeName();
        int main, anc, afterRestartMain;

        using (var host = await startAsync())
        {
            await host.TrackActivity().Timeout(30.Seconds()).SendMessageAndWaitAsync(new PolecatRegisteredReplayMessage(id));
            PolecatRegisteredReplayHandler.Count.ShouldBe(1);

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

        _output.WriteLine($"handler invocations: {PolecatRegisteredReplayHandler.Count}; main Incoming after first run: {main}; after restart: {afterRestartMain}");

        this.ShouldSatisfyAllConditions(
            () => main.ShouldBe(0, "the envelope should not be left Incoming in the MAIN store"),
            () => anc.ShouldBe(0),
            () => PolecatRegisteredReplayHandler.Count.ShouldBe(1, "a handled message was replayed after a restart"));
    }

    // Isolates the cause: the same registered (bare-chain) handler, envelope in the same MAIN store, but with no
    // ancillary session -- so no FlushOutgoingMessagesOnCommit. DurableReceiver's own mark-as-handled then
    // runs against the envelope's store, and nothing is stranded.
    [Fact]
    public async Task control_registered_handler_without_a_session_is_marked_handled_and_not_replayed()
    {
        var id = Guid.NewGuid();
        var typeName = typeof(PolecatRegisteredNoSessionReplayMessage).ToMessageTypeName();

        using (var host = await startAsync())
        {
            await host.TrackActivity().Timeout(30.Seconds()).SendMessageAndWaitAsync(new PolecatRegisteredNoSessionReplayMessage(id));
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

        PolecatRegisteredNoSessionReplayHandler.Count.ShouldBe(1);
    }
}
