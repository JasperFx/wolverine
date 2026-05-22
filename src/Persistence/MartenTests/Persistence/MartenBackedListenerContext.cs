using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Shouldly;
using Wolverine;
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

public class MartenBackedListenerContext : PostgresqlContext, IAsyncLifetime
{
    private readonly IList<Envelope> theEnvelopes = [];
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    private readonly Uri theUri = "tcp://localhost:1111".ToUri();
    private DocumentStore _documentStore = null!;
    private PostgresqlMessageStore _messageStore = null!;
    private DurableReceiver _receiver = null!;
    protected DurabilitySettings theSettings = null!;


    public async Task InitializeAsync()
    {
        _documentStore = DocumentStore.For(opts =>
            opts.Connection(Servers.PostgresConnectionString));

        theSettings = new DurabilitySettings();

        _messageStore = new PostgresqlMessageStore(
            new DatabaseSettings() { ConnectionString = Servers.PostgresConnectionString }, 
            theSettings,
            NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            new NullLogger<PostgresqlMessageStore>());

        await _messageStore.RebuildAsync();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Storage.Returns(_messageStore);
        runtime.Pipeline.Returns(thePipeline);
        runtime.DurabilitySettings.Returns(theSettings);

        _receiver = new DurableReceiver(new LocalQueue("temp"), runtime, runtime.Pipeline);
    }

    public async Task DisposeAsync()
    {
        if (_documentStore is not null)
            await _documentStore.DisposeAsync();
        if (_messageStore is not null)
            await _messageStore.DisposeAsync();
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
        await _receiver.ProcessReceivedMessagesAsync(DateTimeOffset.Now, listener, [.. theEnvelopes]);

        return await _messageStore!.AllIncomingAsync();
    }

    protected void assertEnvelopeWasEnqueued(Envelope envelope)
    {
        thePipeline.Received().InvokeAsync(envelope, _receiver);
    }
}