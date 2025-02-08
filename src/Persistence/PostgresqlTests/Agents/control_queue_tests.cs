using System.Diagnostics;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Tracking;

namespace PostgresqlTests.Agents;

public class control_queue_tests : PostgresqlContext, IAsyncLifetime
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
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync("pgcontrol");
        await conn.CloseAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "pgcontrol");
                opts.ServiceName = "Sender";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "pgcontrol");
                opts.ServiceName = "Receiver";
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var nodeId = _receiver.GetRuntime().Options.UniqueNodeId;
        _receiverUri = new Uri($"dbcontrol://{nodeId}");
    }

    [Fact]
    public async Task control_queue_table_should_exist()
    {
        using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var tables = await conn.ExistingTablesAsync(schemas: ["pgcontrol"]);
        await conn.CloseAsync();

        tables.ShouldContain(x => x.Name == DatabaseConstants.ControlQueueTableName);
    }

    [Fact]
    public async Task send_message_from_one_to_another()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            //.WaitForMessageToBeReceivedAt<Command>(_receiver)
            .ExecuteAndWaitAsync(m => m.EndpointFor(_receiverUri).SendAsync(new Command(10)));

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(Command)).ServiceName
            .ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(Command))
            .ServiceName
            .ShouldBe("Receiver");
    }

    [Fact]
    public async Task request_reply_message_from_one_to_another()
    {
        var (tracked, result) = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(120.Seconds())
            .WaitForMessageToBeReceivedAt<Result>(_sender)
            .InvokeAndWaitAsync<Result>(new Query(13), _receiverUri);

        result.Number.ShouldBe(13);


        tracked.Sent.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(Query)).ServiceName
            .ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(Query)).ServiceName
            .ShouldBe("Receiver");

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(Result)).ServiceName
            .ShouldBe("Receiver");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope.Message.GetType() == typeof(Result))
            .ServiceName
            .ShouldBe("Sender");
    }
}

public record Query(int Number);

public record Result(int Number);

public record Command(int Number);

public static class QueryMessageHandler
{
    public static Result Handle(Query query)
    {
        return new Result(query.Number);
    }

    public static void Handle(Command command)
    {
        Debug.WriteLine($"Got command {command.Number}");
    }

    public static void Handle(Result result)
    {
        Debug.WriteLine($"Got result {result.Number}");
    }
}