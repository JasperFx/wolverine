﻿using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;

namespace MartenTests.Persistence;

public class MartenBackedListenerTests : MartenBackedListenerContext
{
    [Fact]
    public async Task handling_a_single_not_scheduled_envelope()
    {
        var envelope = notScheduledEnvelope();
        var persisted = (await afterReceivingTheEnvelopes()).Single();

        persisted.Status.ShouldBe(EnvelopeStatus.Incoming);
        persisted.OwnerId.ShouldBe(theSettings.AssignedNodeNumber);

        assertEnvelopeWasEnqueued(envelope);
    }

    [Fact]
    public async Task handling_a_single_scheduled_but_expired_envelope()
    {
        var envelope = scheduledButExpiredEnvelope();
        var persisted = (await afterReceivingTheEnvelopes()).Single();

        persisted.Status.ShouldBe(EnvelopeStatus.Incoming);
        persisted.OwnerId.ShouldBe(theSettings.AssignedNodeNumber);

        assertEnvelopeWasEnqueued(envelope);
    }
}

public class MartenBackedListenerContext : PostgresqlContext, IDisposable, IAsyncLifetime
{
    protected readonly IMessageStoreAdmin MessageStoreAdmin =
        new PostgresqlMessageStore(new DatabaseSettings()
                { ConnectionString = Servers.PostgresConnectionString }, new DurabilitySettings(), NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            new NullLogger<PostgresqlMessageStore>());

    protected readonly IList<Envelope> theEnvelopes = new List<Envelope>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    protected readonly DocumentStore theStore;
    protected readonly Uri theUri = "tcp://localhost:1111".ToUri();
    internal DurableReceiver theReceiver;
    protected DurabilitySettings theSettings;


    public MartenBackedListenerContext()
    {
        theStore = DocumentStore.For(opts => { opts.Connection(Servers.PostgresConnectionString); });
    }

    public async Task InitializeAsync()
    {
        theSettings = new DurabilitySettings();


        await MessageStoreAdmin.RebuildAsync();

        var persistence =
            new PostgresqlMessageStore(
                new DatabaseSettings() { ConnectionString = Servers.PostgresConnectionString }, theSettings, NpgsqlDataSource.Create(Servers.PostgresConnectionString),
                new NullLogger<PostgresqlMessageStore>());

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Storage.Returns(persistence);
        runtime.Pipeline.Returns(thePipeline);
        runtime.DurabilitySettings.Returns(theSettings);


        theReceiver = new DurableReceiver(new LocalQueue("temp"), runtime, runtime.Pipeline);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        theStore?.Dispose();
    }

    protected Envelope notScheduledEnvelope()
    {
        var env = new Envelope
        {
            Data = [1, 2, 3, 4],
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        theEnvelopes.Add(env);

        return env;
    }

    protected Envelope scheduledEnvelope()
    {
        var env = new Envelope
        {
            Data = [1, 2, 3, 4],
            ScheduledTime = DateTimeOffset.Now.Add(1.Hours()),
            MessageType = "foo",
            ContentType = EnvelopeConstants.JsonContentType
        };

        theEnvelopes.Add(env);

        return env;
    }

    protected Envelope scheduledButExpiredEnvelope()
    {
        var env = new Envelope
        {
            Data = [1, 2, 3, 4],
            ScheduledTime = DateTimeOffset.Now.Add(-1.Hours()),
            ContentType = EnvelopeConstants.JsonContentType,
            MessageType = "foo"
        };

        theEnvelopes.Add(env);

        return env;
    }

    protected async Task<IReadOnlyList<Envelope>> afterReceivingTheEnvelopes()
    {
        var listener = Substitute.For<IListener>();
        listener.Address.Returns(theUri);
        await theReceiver.ProcessReceivedMessagesAsync(DateTimeOffset.Now, listener, theEnvelopes.ToArray());

        return await MessageStoreAdmin.AllIncomingAsync();
    }

    protected void assertEnvelopeWasEnqueued(Envelope envelope)
    {
        thePipeline.Received().InvokeAsync(envelope, theReceiver);
    }

    protected void assertEnvelopeWasNotEnqueued(Envelope envelope)
    {
        thePipeline.DidNotReceive().InvokeAsync(envelope, theReceiver);
    }
}