using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Util;

namespace MartenTests.Persistence;

public class marten_durability_end_to_end : IAsyncLifetime
{
    private const string SenderSchemaName = "sender";
    private const string ReceiverSchemaName = "receiver";
    private Uri _listener;
    private LightweightCache<string, IHost> _receivers;
    private DocumentStore _receiverStore;
    private LightweightCache<string, IHost> _senders;
    private DocumentStore _sendingStore;

    public async Task InitializeAsync()
    {
        _listener = new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}");

        _receiverStore = DocumentStore.For(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = ReceiverSchemaName;

            opts.Schema.For<TraceDoc>();
        });

        await _receiverStore.Advanced.Clean.CompletelyRemoveAllAsync();
        await _receiverStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        _sendingStore = DocumentStore.For(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = SenderSchemaName;
        });

        var advanced = new DurabilitySettings();

        var logger = new NullLogger<PostgresqlMessageStore>();
        await new PostgresqlMessageStore(new DatabaseSettings()
                    { ConnectionString = Servers.PostgresConnectionString, SchemaName = ReceiverSchemaName }, advanced, NpgsqlDataSource.Create(Servers.PostgresConnectionString),
                logger)
            .RebuildAsync();

        await new PostgresqlMessageStore(new DatabaseSettings()
                    { ConnectionString = Servers.PostgresConnectionString, SchemaName = SenderSchemaName }, advanced, NpgsqlDataSource.Create(Servers.PostgresConnectionString),
                logger)
            .RebuildAsync();

        await _sendingStore.Advanced.Clean.CompletelyRemoveAllAsync();
        await _sendingStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        _receivers = new LightweightCache<string, IHost>(key =>
        {
            // This is bootstrapping a Wolverine application through the
            // normal ASP.Net Core IWebHostBuilder
            return Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Policies.AutoApplyTransactions();
                    opts.DisableConventionalDiscovery();
                    opts.IncludeType<TraceHandler>();

                    opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = ReceiverSchemaName;
                        m.DisableNpgsqlLogging = true;
                    }).IntegrateWithWolverine();

                    opts.ListenForMessagesFrom(_listener).UseDurableInbox();
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

                    opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = SenderSchemaName;
                        m.DisableNpgsqlLogging = true;
                    }).IntegrateWithWolverine();

                    opts.Durability.ScheduledJobPollingTime = 1.Seconds();
                    opts.Durability.ScheduledJobFirstExecution = 0.Seconds();

                    opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
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
        _receiverStore = null;
        _sendingStore.Dispose();
        _sendingStore = null;
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
        using var session = _receiverStore.LightweightSession();
        return session.Query<TraceDoc>().Count();
    }

    protected async Task WaitForMessagesToBeProcessed(int count)
    {
        await using var session = _receiverStore.QuerySession();
        for (var i = 0; i < 200; i++)
        {
            var actual = session.Query<TraceDoc>().Count();
            var envelopeCount = PersistedIncomingCount();


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
        using var conn = _receiverStore.Tenancy.Default.Database.CreateConnection();
        conn.Open();

        return (long)conn.CreateCommand(
                $"select count(*) from receiver.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'")
            .ExecuteScalar();
    }

    protected long PersistedOutgoingCount()
    {
        using var conn = _sendingStore.Tenancy.Default.Database.CreateConnection();
        conn.Open();

        return (long)conn.CreateCommand(
                $"select count(*) from sender.{DatabaseConstants.OutgoingTable}")
            .ExecuteScalar();
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

    [Fact]
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
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; }
}

public class TraceMessage
{
    public string Name { get; set; }
}

[WolverineIgnore]
public class TraceHandler
{
    public void Handle(TraceMessage message, IDocumentSession session)
    {
        session.Store(new TraceDoc { Name = message.Name });
    }
}