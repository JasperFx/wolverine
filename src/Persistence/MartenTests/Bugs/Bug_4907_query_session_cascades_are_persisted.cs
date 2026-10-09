using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

// GH-4907: the IQuerySession permutation of GH-2941. An IQuerySession parameter is served by casting
// the outbox-enrolled IDocumentSession, but CanApply deliberately ignores IQuerySession, so the chain
// gets no SaveChangesAsync. Messages it cascades or publishes are queued on that session and never
// committed: a scheduled one is never handled, and a durable local one only ever runs from memory.
public class Bug_4907_query_session_cascades_are_persisted : PostgresqlContext, IAsyncLifetime
{
    private const string Schema = "query_session_4907";
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(Schema);
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(QuerySessionCascadeHandler))
                    .IncludeType(typeof(QuerySessionPublishHandler))
                    .IncludeType(typeof(QuerySessionOnlyReadsHandler))
                    .IncludeType(typeof(QuerySessionCascadedSink));

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = Schema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private HandlerChain chainFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        return _host.GetRuntime().Handlers.ChainFor<T>()!;
    }

    [Fact]
    public async Task a_query_session_handler_schedules_its_cascading_message()
    {
        // Without the fix this times out: the scheduled envelope never reaches wolverine_incoming_envelopes.
        var tracked = await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<QuerySessionCascaded>(_host)
            .ExecuteAndWaitAsync(_ => _host.MessageBus().PublishAsync(new CascadeAfterQuery(Guid.NewGuid())));

        tracked.Received.MessagesOf<QuerySessionCascaded>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task a_query_session_handler_schedules_what_it_publishes_through_the_bus()
    {
        var tracked = await _host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<QuerySessionCascaded>(_host)
            .ExecuteAndWaitAsync(_ => _host.MessageBus().PublishAsync(new PublishAfterQuery(Guid.NewGuid())));

        tracked.Received.MessagesOf<QuerySessionCascaded>().Count().ShouldBe(1);
    }

    [Fact]
    public void a_query_session_chain_that_sends_messages_gets_its_commit()
    {
        chainFor<CascadeAfterQuery>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
        chainFor<PublishAfterQuery>().Postprocessors.OfType<MethodCall>()
            .ShouldContain(x => x.Method.Name == nameof(IDocumentSession.SaveChangesAsync));
    }

    [Fact]
    public void a_query_session_chain_that_only_reads_stays_non_transactional()
    {
        // A read-only parameter on its own is still not evidence that the chain writes anything.
        chainFor<OnlyRead>().IsTransactional.ShouldBeFalse();
    }
}

public record CascadeAfterQuery(Guid Id);

public record PublishAfterQuery(Guid Id);

public record OnlyRead(Guid Id);

public record QuerySessionCascaded(Guid Id);

public class QuerySessionDoc
{
    public Guid Id { get; set; }
}

public static class QuerySessionCascadeHandler
{
    public static async Task<DeliveryMessage<QuerySessionCascaded>> Handle(CascadeAfterQuery command, IQuerySession session)
    {
        await session.LoadAsync<QuerySessionDoc>(command.Id);
        return new QuerySessionCascaded(command.Id).DelayedFor(2.Seconds());
    }
}

public static class QuerySessionPublishHandler
{
    public static async Task Handle(PublishAfterQuery command, IQuerySession session, IMessageBus bus)
    {
        await session.LoadAsync<QuerySessionDoc>(command.Id);
        await bus.ScheduleAsync(new QuerySessionCascaded(command.Id), 2.Seconds());
    }
}

public static class QuerySessionOnlyReadsHandler
{
    public static async Task Handle(OnlyRead command, IQuerySession session)
    {
        await session.LoadAsync<QuerySessionDoc>(command.Id);
    }
}

public static class QuerySessionCascadedSink
{
    public static void Handle(QuerySessionCascaded message)
    {
    }
}
