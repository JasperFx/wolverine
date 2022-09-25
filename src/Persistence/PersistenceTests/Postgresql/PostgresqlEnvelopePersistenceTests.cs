using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntegrationTests;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using PersistenceTests.Marten;
using PersistenceTests.Marten.Persistence;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Transports.Tcp;
using Xunit;

namespace PersistenceTests.Postgresql;

public class PostgresqlEnvelopePersistenceTests : PostgresqlContext, IDisposable, IAsyncLifetime
{
    public IHost theHost = WolverineHost.For(opts =>
    {
        opts.Services.AddMarten(x =>
        {
            x.Connection(Servers.PostgresConnectionString);
            x.DatabaseSchemaName = "receiver";
        }).IntegrateWithWolverine();

        opts.ListenAtPort(2345).UseDurableInbox();
    });

    private IEnvelopePersistence thePersistence;

    public async Task InitializeAsync()
    {
        var store = theHost.Get<IDocumentStore>();
        await store.Advanced.Clean.CompletelyRemoveAllAsync();
        await theHost.ResetResourceState();

        thePersistence = theHost.Services.GetRequiredService<IEnvelopePersistence>();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        theHost?.Dispose();
    }

    [Fact]
    public async Task get_counts()
    {
        var thePersistor = theHost.Get<PostgresqlEnvelopePersistence>();

        var list = new List<Envelope>();

        // 10 incoming
        for (var i = 0; i < 10; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;

            list.Add(envelope);
        }

        await thePersistor.StoreIncomingAsync(list.ToArray());


        // 7 scheduled
        list.Clear();
        for (var i = 0; i < 7; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Scheduled;

            list.Add(envelope);
        }

        await thePersistor.StoreIncomingAsync(list.ToArray());


        // 3 outgoing
        list.Clear();
        for (var i = 0; i < 3; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;

            list.Add(envelope);
        }

        await thePersistor.StoreOutgoingAsync(list.ToArray(), 0);

        var counts = await thePersistor.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(10);
        counts.Scheduled.ShouldBe(7);
        counts.Outgoing.ShouldBe(3);
    }
    
    [Fact]
    public async Task store_a_single_incoming_envelope()
    {
        var envelope = SqlServer.ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.StoreIncomingAsync(envelope);

        var stored = (await thePersistence.Admin.AllIncomingAsync()).Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(envelope.OwnerId);
        stored.Status.ShouldBe(envelope.Status);
    }
    
    [Fact]
    public async Task store_a_single_incoming_envelope_that_is_a_duplicate()
    {
        var envelope = SqlServer.ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;

        await thePersistence.StoreIncomingAsync(envelope);

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(async () =>
        {
            await thePersistence.StoreIncomingAsync(envelope);
        });
    }

    [Fact]
    public async Task store_a_single_outgoing_envelope()
    {
        var envelope = SqlServer.ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Outgoing;

        await thePersistence.StoreOutgoingAsync(envelope, 5890);

        var stored = (await thePersistence.Admin.AllOutgoingAsync())
            .Single();

        stored.Id.ShouldBe(envelope.Id);
        stored.OwnerId.ShouldBe(5890);
        stored.Status.ShouldBe(envelope.Status);
    }
    

    [Fact]
    public async Task mark_envelope_as_handled()
    {
        var envelope = ObjectMother.Envelope();

        await thePersistence.StoreIncomingAsync(envelope);

        await thePersistence.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var counts = await thePersistence.Admin.FetchCountsAsync();
        
        counts.Incoming.ShouldBe(0);
        counts.Scheduled.ShouldBe(0);
        counts.Handled.ShouldBe(1);

    }
}