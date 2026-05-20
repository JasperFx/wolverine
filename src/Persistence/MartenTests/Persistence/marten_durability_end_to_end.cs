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
    private Uri _listener = null!;
    private LightweightCache<string, IHost> _receivers = null!;
    private DocumentStore _receiverStore = null!;
    private LightweightCache<string, IHost> _senders = null!;
    private DocumentStore _sendingStore = null!;
    private PostgresqlMessageStore? _receiverMessageStore;
    private PostgresqlMessageStore? _senderMessageStore;

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
        _receiverMessageStore = new PostgresqlMessageStore(new DatabaseSettings()
                    { ConnectionString = Servers.PostgresConnectionString, SchemaName = ReceiverSchemaName }, advanced, NpgsqlDataSource.Create(Servers.PostgresConnectionString),
                logger);
        await _receiverMessageStore.RebuildAsync();

        _senderMessageStore = new PostgresqlMessageStore(new DatabaseSettings()
                    { ConnectionString = Servers.PostgresConnectionString, SchemaName = SenderSchemaName }, advanced, NpgsqlDataSource.Create(Servers.PostgresConnectionString),
                logger);
        await _senderMessageStore.RebuildAsync();

        await _sendingStore.Advanced.Clean.CompletelyRemoveAllAsync();
        await _sendingStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        _receivers = new LightweightCache<string, IHost>(key =>
        {
            // This is bootstrapping a Wolverine application through the
            // normal ASP.Net Core IWebHostBuilder
            return Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;
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
                    opts.Durability.Mode = DurabilityMode.Solo;
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
        _receiverStore = null!;
        _sendingStore.Dispose();
        _sendingStore = null!;

        if (_receiverMessageStore != null)
        {
            await _receiverMessageStore.DisposeAsync();
            _receiverMessageStore = null;
        }

        if (_senderMessageStore != null)
        {
            await _senderMessageStore.DisposeAsync();
            _senderMessageStore = null;
        }
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

    protected async Task<int> ReceivedMessageCount()
    {
        await using var session = _receiverStore.LightweightSession();
        return await session.Query<TraceDoc>().CountAsync();
    }

    protected async Task WaitForMessagesToBeProcessed(int count)
    {
        await using var session = _receiverStore.QuerySession();
        for (var i = 0; i < 480; i++)
        {
            var actual = await session.Query<TraceDoc>().CountAsync();
            var envelopeCount = await PersistedIncomingCount();

            if (actual == count && envelopeCount == 0)
                return;

            await Task.Delay(250);
        }

        throw new Exception("All messages were not received");
    }

    protected async Task<long> PersistedIncomingCount()
    {
        await using var conn = _receiverStore.Tenancy.Default.Database.CreateConnection();
        await conn.OpenAsync();

        var command = conn.CreateCommand(
            $"select count(*) from receiver.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'");

        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
    }

    protected async Task<long> PersistedOutgoingCount()
    {
        await using var conn = _receiverStore.Tenancy.Default.Database.CreateConnection();
        await conn.OpenAsync();

        var command = conn.CreateCommand(
            $"select count(*) from sender.{DatabaseConstants.OutgoingTable}");

        var count = await command.ExecuteScalarAsync();
        return Convert.ToInt64(count);
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
        (await PersistedOutgoingCount()).ShouldBe(10);
        StartReceiver("Receiver1");
        StartSender("Sender2");
        await WaitForMessagesToBeProcessed(10);
        (await PersistedIncomingCount()).ShouldBe(0);
        (await PersistedOutgoingCount()).ShouldBe(0);
        (await ReceivedMessageCount()).ShouldBe(10);
    }

    [Fact]
    public async Task sending_resumes_when_the_receiver_is_detected()
    {
        StartSender("Sender1");
        await SendMessages("Sender1", 5);
        StartReceiver("Receiver1");
        await WaitForMessagesToBeProcessed(5);
        (await PersistedIncomingCount()).ShouldBe(0);
        (await PersistedOutgoingCount()).ShouldBe(0);
        (await ReceivedMessageCount()).ShouldBe(5);
    }
}

public class TraceDoc
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = null!;
}

public class TraceMessage
{
    public string Name { get; set; } = null!;
}

[WolverineIgnore]
public class TraceHandler
{
    public void Handle(TraceMessage message, IDocumentSession session)
    {
        session.Store(new TraceDoc { Name = message.Name });
    }
}