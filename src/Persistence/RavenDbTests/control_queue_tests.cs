using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.RavenDb;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace RavenDbTests;

[Collection("raven")]
public class control_queue_tests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _sender = null!;
    private IHost _receiver = null!;
    private Uri _receiverUri = null!;

    public control_queue_tests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        // Both nodes must share the same RavenDB database so that control messages
        // written by one node are visible to the other node's control listener.
        _store = _fixture.StartRavenStore();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IDocumentStore>(_store);
                opts.UseRavenDbPersistence();
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.ServiceName = "Sender";
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IDocumentStore>(_store);
                opts.UseRavenDbPersistence();
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.ServiceName = "Receiver";
            }).StartAsync();

        var nodeId = _receiver.GetRuntime().Options.UniqueNodeId;
        _receiverUri = new Uri($"ravencontrol://{nodeId}");
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        _sender.Dispose();
        await _receiver.StopAsync();
        _receiver.Dispose();
    }

    [Fact]
    public void control_endpoint_is_wired_up_in_balanced_mode()
    {
        // Regression: in Balanced mode a null NodeControlEndpoint causes
        // WolverineNode.For to throw "ControlEndpoint cannot be null for this usage".
        var endpoint = _sender.GetRuntime().Options.Transports.NodeControlEndpoint;
        endpoint.ShouldNotBeNull();
        endpoint.Uri.Scheme.ShouldBe("ravencontrol");
    }

    [Fact]
    public async Task send_message_from_one_to_another()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.EndpointFor(_receiverUri).SendAsync(new Command(10)));

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message?.GetType() == typeof(Command)).ServiceName!
            .ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message?.GetType() == typeof(Command))
            .ServiceName!
            .ShouldBe("Receiver");
    }

    [Fact]
    public async Task request_reply_message_from_one_to_another()
    {
        var (tracked, result) = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .InvokeAndWaitAsync<Result>(new Query(13), _receiverUri);

        result!.Number.ShouldBe(13);

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(Query)).ServiceName!
            .ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(Query)).ServiceName!
            .ShouldBe("Receiver");

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(Result)).ServiceName!
            .ShouldBe("Receiver");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(Result))
            .ServiceName!
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
