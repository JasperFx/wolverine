using Fisher;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace FisherTests;

// Fisher parallel of MartenTests.Bugs.Bug_4907_query_session_cascades_are_persisted (#4910). An
// IQuerySession parameter is served by casting the outbox-enrolled IDocumentSession, but CanApply ignored
// IQuerySession, so the chain got no SaveChangesAsync. Messages it cascades or publishes are queued on that
// session and never committed: a scheduled one is never handled, and a durable local one only ever runs
// from memory.
public class Bug_4907_query_session_cascades_are_persisted : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("query_session_4907");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FiQuerySessionCascadeHandler))
                    .IncludeType(typeof(FiQuerySessionPublishHandler))
                    .IncludeType(typeof(FiQuerySessionOnlyReadsHandler))
                    .IncludeType(typeof(FiQuerySessionCascadedSink));

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    private HandlerChain chainFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        return _host.GetRuntime().Handlers.ChainFor<T>()!;
    }

    [Fact]
    public async Task a_query_session_handler_schedules_its_cascading_message()
    {
        // Without the fix this times out: the scheduled envelope never reaches the incoming table.
        var tracked = await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<FiQuerySessionCascaded>(_host)
            .ExecuteAndWaitAsync(_ => _host.MessageBus().PublishAsync(new FiCascadeAfterQuery(Guid.NewGuid())));

        tracked.Received.MessagesOf<FiQuerySessionCascaded>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_query_session_handler_schedules_what_it_publishes_through_the_bus()
    {
        var tracked = await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<FiQuerySessionCascaded>(_host)
            .ExecuteAndWaitAsync(_ => _host.MessageBus().PublishAsync(new FiPublishAfterQuery(Guid.NewGuid())));

        tracked.Received.MessagesOf<FiQuerySessionCascaded>().Count().ShouldBe(1);
    }

    [Fact]
    public void a_query_session_chain_that_sends_messages_gets_its_commit()
    {
        chainFor<FiCascadeAfterQuery>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
        chainFor<FiPublishAfterQuery>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
    }

    [Fact]
    public void a_query_session_chain_that_only_reads_stays_non_transactional()
    {
        // A read-only parameter on its own is still not evidence that the chain writes anything.
        chainFor<FiOnlyRead>().IsTransactional.ShouldBeFalse();
    }
}

public record FiCascadeAfterQuery(Guid Id);

public record FiPublishAfterQuery(Guid Id);

public record FiOnlyRead(Guid Id);

public record FiQuerySessionCascaded(Guid Id);

public class FiQuerySessionDoc
{
    public Guid Id { get; set; }
}

public static class FiQuerySessionCascadeHandler
{
    public static async Task<DeliveryMessage<FiQuerySessionCascaded>> Handle(FiCascadeAfterQuery command, IQuerySession session)
    {
        await session.LoadAsync<FiQuerySessionDoc>(command.Id);
        return new FiQuerySessionCascaded(command.Id).DelayedFor(2.Seconds());
    }
}

public static class FiQuerySessionPublishHandler
{
    public static async Task Handle(FiPublishAfterQuery command, IQuerySession session, IMessageBus bus)
    {
        await session.LoadAsync<FiQuerySessionDoc>(command.Id);
        await bus.ScheduleAsync(new FiQuerySessionCascaded(command.Id), 2.Seconds());
    }
}

public static class FiQuerySessionOnlyReadsHandler
{
    public static async Task Handle(FiOnlyRead command, IQuerySession session)
    {
        await session.LoadAsync<FiQuerySessionDoc>(command.Id);
    }
}

public static class FiQuerySessionCascadedSink
{
    public static void Handle(FiQuerySessionCascaded message)
    {
    }
}
