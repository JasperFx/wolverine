using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Core;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Util;

namespace SqlServerTests;

public class sqlserver_durability_end_to_end : IAsyncLifetime
{
    private const string SenderSchemaName = "sender";
    private const string ReceiverSchemaName = "receiver";
    private Uri _listener;
    private LightweightCache<string, IHost> _receivers;

    private LightweightCache<string, IHost> _senders;

    public async Task InitializeAsync()
    {
        _listener = new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}");

        await new SqlServerMessageStore(
                new DatabaseSettings()
                    { ConnectionString = Servers.SqlServerConnectionString, SchemaName = ReceiverSchemaName },
                new DurabilitySettings(), new NullLogger<SqlServerMessageStore>(), Array.Empty<SagaTableDefinition>())
            .RebuildAsync();

        await new SqlServerMessageStore(

                    new DatabaseSettings(){ ConnectionString = Servers.SqlServerConnectionString, SchemaName = SenderSchemaName },
                new DurabilitySettings(), new NullLogger<SqlServerMessageStore>(), Array.Empty<SagaTableDefinition>())
            .RebuildAsync();

        await buildTraceDocTable();

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

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, ReceiverSchemaName);

                    opts.ListenForMessagesFrom(_listener).UseDurableInbox();

                    opts.Services.AddResourceSetupOnStartup();
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

                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, SenderSchemaName);

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
    }

    private async Task buildTraceDocTable()
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        await conn.CreateCommand(@"
IF OBJECT_ID('receiver.trace_doc', 'U') IS NOT NULL
  drop table receiver.trace_doc;

").ExecuteNonQueryAsync();

        await conn.CreateCommand(@"
create table receiver.trace_doc
(
	id uniqueidentifier not null
		primary key,
	name varchar(100) not null
);

").ExecuteNonQueryAsync();

        await conn.CloseAsync();
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
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();
        return (int)conn.CreateCommand("select count(*) from receiver.trace_doc").ExecuteScalar();
    }

    protected async Task WaitForMessagesToBeProcessed(int count)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        for (var i = 0; i < 200; i++)
        {
            var actual = (int)conn.CreateCommand("select count(*) from receiver.trace_doc").ExecuteScalar();
            var envelopeCount = PersistedIncomingCount();

            Trace.WriteLine($"waitForMessages: {actual} actual & {envelopeCount} incoming envelopes");

            if (actual == count && envelopeCount == 0)
            {
                await conn.CloseAsync();
                return;
            }

            await Task.Delay(250);
        }

        throw new Exception("All messages were not received");
    }

    protected long PersistedIncomingCount()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();

        return (int)conn.CreateCommand(
                $"select count(*) from receiver.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'")
            .ExecuteScalar();
    }

    protected long PersistedOutgoingCount()
    {
        using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        conn.Open();

        return (int)conn.CreateCommand(
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
    public async Task Handle(TraceMessage message, DatabaseSettings settings)
    {
        using var conn = new SqlConnection(settings.ConnectionString);
        await conn.OpenAsync();

        var traceDoc = new TraceDoc { Name = message.Name };

        await conn.CreateCommand("insert into receiver.trace_doc (id, name) values (@id, @name)")
            .With("id", traceDoc.Id)
            .With("name", traceDoc.Name)
            .ExecuteNonQueryAsync();

        await conn.CloseAsync();
    }
}