using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace PolecatTests.Bugs;

// Polecat parallel of MartenTests.Bugs.Bug_4907_query_session_cascades_are_persisted (#4910). An
// IQuerySession parameter is served by casting the outbox-enrolled IDocumentSession, but CanApply ignored
// IQuerySession, so the chain got no SaveChangesAsync. Messages it cascades or publishes are queued on that
// session and never committed: a scheduled one is never handled, and a durable local one only ever runs
// from memory.
public class Bug_4907_query_session_cascades_are_persisted : IClassFixture<PolecatQuerySessionCascadeContext>
{
    private readonly PolecatQuerySessionCascadeContext _context;

    public Bug_4907_query_session_cascades_are_persisted(PolecatQuerySessionCascadeContext context)
    {
        _context = context;
    }

    private IHost theHost => _context.Host;

    private HandlerChain chainFor<T>()
    {
        theHost.GetRuntime().Handlers.HandlerFor<T>();
        return theHost.GetRuntime().Handlers.ChainFor<T>()!;
    }

    [Fact]
    public async Task a_query_session_handler_schedules_its_cascading_message()
    {
        // Without the fix this times out: the scheduled envelope never reaches the incoming table.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcQuerySessionCascaded>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new PcCascadeAfterQuery(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcQuerySessionCascaded>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_query_session_handler_schedules_what_it_publishes_through_the_bus()
    {
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<PcQuerySessionCascaded>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new PcPublishAfterQuery(Guid.NewGuid())));

        tracked.Received.MessagesOf<PcQuerySessionCascaded>().Count().ShouldBe(1);
    }

    [Fact]
    public void a_query_session_chain_that_sends_messages_gets_its_commit()
    {
        chainFor<PcCascadeAfterQuery>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
        chainFor<PcPublishAfterQuery>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
    }

    [Fact]
    public void a_query_session_chain_that_only_reads_stays_non_transactional()
    {
        // A read-only parameter on its own is still not evidence that the chain writes anything.
        chainFor<PcOnlyRead>().IsTransactional.ShouldBeFalse();
    }
}

public class PolecatQuerySessionCascadeContext : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PcQuerySessionCascadeHandler))
                    .IncludeType(typeof(PcQuerySessionPublishHandler))
                    .IncludeType(typeof(PcQuerySessionOnlyReadsHandler))
                    .IncludeType(typeof(PcQuerySessionCascadedSink));

                opts.Services.AddPolecat(m =>
                    {
                        // See Bug_2941_read_aggregate_scheduled_cascade for why Timeout=5 is bumped.
                        m.ConnectionString = Servers.SqlServerConnectionString.Replace("Timeout=5", "Timeout=30");
                        m.DatabaseSchemaName = "query_session_4907";
                        m.UseNativeJsonType = false;
                    })
                    .IntegrateWithWolverine(integration =>
                    {
                        integration.MessageStorageSchemaName = "query_session_4907_wol";
                    });
            }).StartAsync();

        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)store).Database.ApplyAllConfiguredChangesToDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

public record PcCascadeAfterQuery(Guid Id);

public record PcPublishAfterQuery(Guid Id);

public record PcOnlyRead(Guid Id);

public record PcQuerySessionCascaded(Guid Id);

public class PcQuerySessionDoc
{
    public Guid Id { get; set; }
}

public static class PcQuerySessionCascadeHandler
{
    public static async Task<DeliveryMessage<PcQuerySessionCascaded>> Handle(PcCascadeAfterQuery command, IQuerySession session)
    {
        await session.LoadAsync<PcQuerySessionDoc>(command.Id);
        return new PcQuerySessionCascaded(command.Id).DelayedFor(2.Seconds());
    }
}

public static class PcQuerySessionPublishHandler
{
    public static async Task Handle(PcPublishAfterQuery command, IQuerySession session, IMessageBus bus)
    {
        await session.LoadAsync<PcQuerySessionDoc>(command.Id);
        await bus.ScheduleAsync(new PcQuerySessionCascaded(command.Id), 2.Seconds());
    }
}

public static class PcQuerySessionOnlyReadsHandler
{
    public static async Task Handle(PcOnlyRead command, IQuerySession session)
    {
        await session.LoadAsync<PcQuerySessionDoc>(command.Id);
    }
}

public static class PcQuerySessionCascadedSink
{
    public static void Handle(PcQuerySessionCascaded message)
    {
    }
}
