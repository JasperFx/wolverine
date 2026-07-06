using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
// JasperFx 2.0 lifted DocumentAlreadyExistsException out of Marten.Exceptions
// into the JasperFx namespace; Marten throws the lifted type.
using DocumentAlreadyExistsException = JasperFx.DocumentAlreadyExistsException;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

// When a durable-local-queue handler enlists the Marten outbox and SaveChangesAsync
// rolls back (here: a duplicate document insert violates the unique PK), a
// .Discard() error policy must still clear the incoming envelope. The bug was that
// FlushOutgoingMessagesOnCommit.BeforeSaveChangesAsync flipped the in-memory
// Envelope.Status to Handled BEFORE the commit; on rollback the DB row stayed
// 'Incoming' but the stale in-memory flag made DurableReceiver's _markAsHandled
// optimization skip the real UPDATE, stranding the row as 'Incoming' forever.
public class Bug_discard_after_failed_outbox_commit : PostgresqlContext, IAsyncLifetime
{
    private const string Schema = "discard_failed_commit";
    private IHost _host = null!;

    public async Task InitializeAsync()
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
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DoThingHandler));

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Policies.UseDurableLocalQueues();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = Schema;
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task discarded_duplicate_should_not_be_stuck_in_incoming()
    {
        var store = _host.Services.GetRequiredService<IMessageStore>();
        await store.Admin.ClearAllAsync();

        var id = Guid.NewGuid().ToString();

        // 1st send: inserts Marker(id), commits, envelope marked Handled.
        await _host.TrackActivity().Timeout(30.Seconds())
            .ExecuteAndWaitAsync(c => c.PublishAsync(new DoThing(id)));

        // 2nd send (duplicate): MartenOps.Insert hits the unique PK inside the outbox
        // SaveChangesAsync => DocumentAlreadyExistsException => .Discard().
        await _host.TrackActivity().Timeout(30.Seconds()).DoNotAssertOnExceptionsDetected()
            .ExecuteAndWaitAsync(c => c.PublishAsync(new DoThing(id)));

        var counts = await store.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
    }
}

public record DoThing(string Id);

// Marten document; `Id` is the identity => unique PK. A 2nd Insert of the same Id throws.
public record Marker(string Id);

public static class DoThingHandler
{
    public static void Configure(HandlerChain chain)
        => chain.OnException<DocumentAlreadyExistsException>().Discard();

    public static IMartenOp Handle(DoThing message)
        => MartenOps.Insert(new Marker(message.Id));
}
