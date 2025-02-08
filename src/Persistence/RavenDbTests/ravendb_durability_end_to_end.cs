using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.ServerWide.Operations.Configuration;
using Raven.TestDriver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

public class ravendb_durability_end_to_end : RavenTestDriver, IAsyncLifetime
{
    private const string SenderSchemaName = "sender";
    private const string ReceiverSchemaName = "receiver";
    private Uri _listener;
    private LightweightCache<string, IHost> _receivers;

    private LightweightCache<string, IHost> _senders;
    private IDocumentStore _receiverStore;
    private IDocumentStore _senderStore;

    public async Task InitializeAsync()
    {
        _listener = new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}");

        _receiverStore = GetDocumentStore();
        _senderStore = GetDocumentStore();

        _receivers = new LightweightCache<string, IHost>(key =>
        {
            // This is bootstrapping a Wolverine application through the
            // normal ASP.Net Core IWebHostBuilder
            return Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.DisableConventionalDiscovery();
                    opts.IncludeType<TraceHandler>();
                    opts.Policies.AutoApplyTransactions();

                    opts.CodeGeneration.InsertFirstPersistenceStrategy<RavenDbPersistenceFrameProvider>();
                    opts.Services.AddSingleton<IMessageStore>(s => new RavenDbMessageStore(_receiverStore, s.GetRequiredService<WolverineOptions>()));

                    // Leave it as a lambda so it doesn't get disposed
                    opts.Services.AddSingleton<IDocumentStore>(s => _receiverStore);
                    
                    opts.ListenForMessagesFrom(_listener).UseDurableInbox();

                    opts.Services.AddResourceSetupOnStartup();
                    
                    opts.UseTcpForControlEndpoint();
                })
                .Start();
        });

        _senders = new LightweightCache<string, IHost>(key =>
        {
            return Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.DisableConventionalDiscovery();
                    opts.Policies.AutoApplyTransactions();

                    opts.Publish(x => x.Message<TraceMessage>().To(_listener)
                        .UseDurableOutbox());

                    opts.UseTcpForControlEndpoint();
                    
                    opts.CodeGeneration.InsertFirstPersistenceStrategy<RavenDbPersistenceFrameProvider>();
                    opts.Services.AddSingleton<IMessageStore>(s => new RavenDbMessageStore(_senderStore, s.GetRequiredService<WolverineOptions>()));
                    
                    // Leave it as a lambda so it doesn't get disposed
                    opts.Services.AddSingleton<IDocumentStore>(s => _senderStore);

                    opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                    opts.Durability.ScheduledJobFirstExecution = 0.Seconds();

                    opts.Services.AddResourceSetupOnStartup();
                })
                .Start();
        });
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _receivers)
        {
            await host.StopAsync();
            host.Dispose();
        }

        _receivers.Clear();

        foreach (var host in _senders)
        {
            await host.StopAsync();
            host.Dispose();
        }

        _senders.Clear();
        
        _receiverStore.Dispose();
        _senderStore.Dispose();
    }

    protected void StartReceiver(string name)
    {
        _receivers.FillDefault(name);
    }

    protected void StartSender(string name)
    {
        _senders.FillDefault(name);
    }

    protected ValueTask SendFrom(string sender, string name)
    {
        return _senders[sender].Services.GetRequiredService<IMessageContext>()
            .SendAsync(new TraceMessage { Name = name });
    }

    protected async Task SendMessages(string sender, int count)
    {
        var runtime = _senders[sender];

        for (var i = 0; i < count; i++)
        {
            var msg = new TraceMessage { Name = Guid.NewGuid().ToString() };
            await runtime.MessageBus().SendAsync(msg);
        }
    }

    protected int ReceivedMessageCount()
    {
        using var session = _receiverStore.OpenSession();
        return session.Query<TraceDoc>().Customize(x => x.WaitForNonStaleResults()).Count();
    }

    protected async Task WaitForMessagesToBeProcessed(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            var outgoing = PersistedOutgoingCount();
            var actual = ReceivedMessageCount();
            var envelopeCount = PersistedIncomingCount();

            Trace.WriteLine($"waitForMessages: {actual} received, {envelopeCount} incoming envelopes, {outgoing} outgoing envelopes");

            if (actual == count && envelopeCount == 0)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new Exception("All messages were not received");
    }

    protected long PersistedIncomingCount()
    {
        using var session = _receiverStore.OpenSession();
        return session
            .Query<IncomingMessage>()
            .Customize(x => x.WaitForNonStaleResults())
            .Count(x => x.Status == EnvelopeStatus.Incoming);
    }

    protected long PersistedOutgoingCount()
    {
        using var session = _senderStore.OpenSession();
        return session.Query<OutgoingMessage>().Customize(x => x.WaitForNonStaleResults()).Count();
    }

    protected async Task StopReceiver(string name)
    {
        var receiver = _receivers[name];
        await receiver.StopAsync();
        receiver.Dispose();
        _receivers.Remove(name);
    }

    protected async Task StopSender(string name)
    {
        var sender = _senders[name];
        await sender.StopAsync();
        sender.Dispose();
        _senders.Remove(name);
    }

    [Fact] // This test "blinks"
    public async Task sending_recovered_messages_when_sender_starts_up()
    {
        StartSender("Sender1");
        await SendMessages("Sender1", 10);
        await StopSender("Sender1");
        PersistedOutgoingCount().ShouldBe(10);
        StartReceiver("Receiver1");
        StartSender("Sender2");
        await WaitForMessagesToBeProcessed(10);
        PersistedIncomingCount().ShouldBe(0);
        PersistedOutgoingCount().ShouldBe(0);
        ReceivedMessageCount().ShouldBe(10);
    }

    [Fact]
    public async Task sending_resumes_when_the_receiver_is_detected()
    {
        StartSender("Sender1");
        await SendMessages("Sender1", 5);
        StartReceiver("Receiver1");
        await WaitForMessagesToBeProcessed(5);
        PersistedIncomingCount().ShouldBe(0);
        PersistedOutgoingCount().ShouldBe(0);
        ReceivedMessageCount().ShouldBe(5);
    }
}

public class TraceDoc
{
    public string Id { get; set; }
    public string Name { get; set; }
}

public class TraceMessage
{
    public string Name { get; set; }
}

[WolverineIgnore]
public class TraceHandler
{
    public async Task Handle(TraceMessage message, IAsyncDocumentSession session)
    {
        var traceDoc = new TraceDoc { Name = message.Name };
        await session.StoreAsync(traceDoc);
    }
}