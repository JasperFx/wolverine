using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace SlowTests;

public class invoke_async_with_delivery_options : IAsyncLifetime
{
    private IHost _publisher;
    private IHost _receiver;

    public async Task InitializeAsync()
    {
        var publisherPort = PortFinder.GetAvailablePort();
        var receiverPort = PortFinder.GetAvailablePort();


        _publisher = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToPort(receiverPort);
                opts.ListenAtPort(publisherPort);
            }).StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(receiverPort);
            }).StartAsync();

    }

    public async Task DisposeAsync()
    {
        await _publisher.StopAsync();
        await _receiver.StopAsync();
    }

    [Fact]
    public async Task invoke_locally()
    {
        var bus = _receiver.MessageBus();
        await bus.InvokeAsync(new WithHeaders(),
            new DeliveryOptions { TenantId = "millers" }.WithHeader("name", "Chewie")
                .WithHeader("breed", "indeterminate"));
    }

    [Fact]
    public async Task invoke_with_expected_outcome_locally()
    {
        var bus = _receiver.MessageBus();
        var answer = await bus.InvokeAsync<Answer>(new DoMath(3, 4, "blue", "tom"),
            new DeliveryOptions { TenantId = "blue" }.WithHeader("user-id", "tom"));
        
        answer.Sum.ShouldBe(7);
    }
    
    [Fact]
    public async Task invoke_remotely()
    {
        var tracked = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c => c.InvokeAsync(new WithHeaders(),
                new DeliveryOptions { TenantId = "millers" }.WithHeader("name", "Chewie")
                    .WithHeader("breed", "indeterminate")));

        var envelope = tracked.Received.SingleEnvelope<WithHeaders>();
        envelope.TenantId.ShouldBe("millers");
        envelope.Headers["name"].ShouldBe("Chewie");
    }

    [Fact]
    public async Task invoke_with_expected_outcome_remotely()
    {
        Answer answer = null;
        Func<IMessageContext, Task> action = async c => answer = await c.InvokeAsync<Answer>(new DoMath(3, 4, "blue", "tom"),
            new DeliveryOptions { TenantId = "blue" }.WithHeader("user-id", "tom"));
        var tracked = await _publisher.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(action);

        var envelope = tracked.Received.SingleEnvelope<DoMath>();
        envelope.TenantId.ShouldBe("blue");
        envelope.Headers["user-id"].ShouldBe("tom");
        
        answer.Sum.ShouldBe(7);
    }
}

public record WithHeaders;
public record DoMath(int X, int Y, string ExpectedTenantId, string UserIdHeader);
public record Answer(int Sum, int Product);

public static class InvokeMessageHandler
{
    public static void Handle(WithHeaders message, Envelope envelope)
    {
        // Our family dog
        envelope.Headers["name"].ShouldBe("Chewie");
        envelope.Headers["breed"].ShouldBe("indeterminate");
        envelope.TenantId.ShouldBe("millers");
    }

    public static Answer Handle(DoMath message, Envelope envelope)
    {
        envelope.TenantId.ShouldBe(message.ExpectedTenantId);
        envelope.Headers["user-id"].ShouldBe(message.UserIdHeader);
        return new Answer(message.X + message.Y, message.X * message.Y);
    }
}