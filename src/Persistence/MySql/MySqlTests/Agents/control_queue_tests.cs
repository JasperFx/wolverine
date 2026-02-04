using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace MySqlTests.Agents;

[Collection("mysql")]
public class control_queue_tests : MySqlContext, IAsyncLifetime
{
    private static IHost _sender;
    private static IHost _receiver;
    private static Uri _receiverUri;

    public async Task InitializeAsync()
    {
        await dropControlSchema();
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    private static async Task dropControlSchema()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP DATABASE IF EXISTS `mysqlcontrol`";
        await cmd.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "mysqlcontrol");
                opts.ServiceName = "Sender";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithMySql(Servers.MySqlConnectionString, "mysqlcontrol");
                opts.ServiceName = "Receiver";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var nodeId = _receiver.GetRuntime().Options.UniqueNodeId;
        _receiverUri = new Uri($"dbcontrol://{nodeId}");
    }

    [Fact]
    public async Task control_queue_table_should_exist()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'mysqlcontrol'
            AND table_name LIKE 'wolverine%'";

        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }
        await conn.CloseAsync();

        tables.ShouldContain(DatabaseConstants.ControlQueueTableName);
    }

    [Fact]
    public async Task send_message_from_one_to_another()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(10.Seconds())
            .ExecuteAndWaitAsync(m => m.EndpointFor(_receiverUri).SendAsync(new MySqlCommand(10)));

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope.Message?.GetType() == typeof(MySqlCommand)).ServiceName
            .ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope.Message?.GetType() == typeof(MySqlCommand))
            .ServiceName
            .ShouldBe("Receiver");
    }

    [Fact]
    public async Task request_reply_message_from_one_to_another()
    {
        var (tracked, result) = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(120.Seconds())
            .InvokeAndWaitAsync<MySqlResult>(new MySqlQuery(13), _receiverUri);

        result.Number.ShouldBe(13);


        tracked.Sent.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(MySqlQuery)).ServiceName
            .ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(MySqlQuery)).ServiceName
            .ShouldBe("Receiver");

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(MySqlResult)).ServiceName
            .ShouldBe("Receiver");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(MySqlResult))
            .ServiceName
            .ShouldBe("Sender");
    }
}

public record MySqlQuery(int Number);

public record MySqlResult(int Number);

public record MySqlCommand(int Number);

public static class MySqlQueryMessageHandler
{
    public static MySqlResult Handle(MySqlQuery query)
    {
        return new MySqlResult(query.Number);
    }

    public static void Handle(MySqlCommand command)
    {
        Debug.WriteLine($"Got command {command.Number}");
    }

    public static void Handle(MySqlResult result)
    {
        Debug.WriteLine($"Got result {result.Number}");
    }
}
