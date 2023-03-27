using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Wolverine.Util;

namespace PersistenceTests.SqlServer.Persistence;

public class SqlServerBackedListenerContext : SqlServerContext
{
    protected readonly IList<Envelope> theEnvelopes = new List<Envelope>();
    private readonly IHandlerPipeline thePipeline = Substitute.For<IHandlerPipeline>();
    protected readonly Uri theUri = "tcp://localhost:1111".ToUri();
    protected SqlServerSettings mssqlSettings;
    protected IMessageDatabase thePersistence;
    internal DurableReceiver theReceiver;
    protected DurabilitySettings theSettings;


    public SqlServerBackedListenerContext()
    {
        theSettings = new DurabilitySettings();

        mssqlSettings = new SqlServerSettings
        {
            ConnectionString = Servers.SqlServerConnectionString
        };

        thePersistence =
            new SqlServerMessageStore(mssqlSettings, theSettings,
                new NullLogger<SqlServerMessageStore>());

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Storage.Returns(thePersistence);
        runtime.Pipeline.Returns(thePipeline);
        runtime.DurabilitySettings.Returns(theSettings);


        theReceiver = new DurableReceiver(new LocalQueue("temp"), runtime, thePipeline);
    }

    protected Envelope notScheduledEnvelope()
    {
        var env = new Envelope
        {
            Data = new byte[] { 1, 2, 3, 4 },
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
            Data = new byte[] { 1, 2, 3, 4 },
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
            Data = new byte[] { 1, 2, 3, 4 },
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

        return await thePersistence.Admin.AllIncomingAsync();
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