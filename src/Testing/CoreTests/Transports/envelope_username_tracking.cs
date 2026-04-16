using JasperFx.Core;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Xunit;

namespace CoreTests.Transports;

public class envelope_username_tracking
{
    [Fact]
    public async Task username_is_propagated_through_tcp_transport()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.DisableConventionalDiscovery();
            opts.IncludeType(typeof(UserNameTrackingHandler));
            opts.EnableRelayOfUserName = true;

            opts.Publish(x =>
            {
                x.Message<UserNameTrackingMessage>();
                x.ToPort(2399);
            });

            opts.ListenAtPort(2399);
        });

        UserNameTrackingHandler.ReceivedUserName = null;

        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c =>
            {
                // Set UserName on the message context — this gets relayed to outgoing envelopes
                c.UserName = "testuser@example.com";

                return c.EndpointFor("tcp://localhost:2399".ToUri())
                    .SendAsync(new UserNameTrackingMessage("hello"));
            });

        session.FindSingleTrackedMessageOfType<UserNameTrackingMessage>(MessageEventType.MessageSucceeded)
            .ShouldNotBeNull();

        UserNameTrackingHandler.ReceivedUserName.ShouldBe("testuser@example.com");
    }

    [Fact]
    public async Task username_is_not_propagated_when_relay_disabled()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.DisableConventionalDiscovery();
            opts.IncludeType(typeof(UserNameTrackingHandler));
            // EnableRelayOfUserName defaults to false

            opts.Publish(x =>
            {
                x.Message<UserNameTrackingMessage>();
                x.ToPort(2398);
            });

            opts.ListenAtPort(2398);
        });

        UserNameTrackingHandler.ReceivedUserName = null;

        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c =>
            {
                c.UserName = "should-not-arrive@example.com";

                return c.EndpointFor("tcp://localhost:2398".ToUri())
                    .SendAsync(new UserNameTrackingMessage("hello"));
            });

        session.FindSingleTrackedMessageOfType<UserNameTrackingMessage>(MessageEventType.MessageSucceeded)
            .ShouldNotBeNull();

        UserNameTrackingHandler.ReceivedUserName.ShouldBeNull();
    }
}

public record UserNameTrackingMessage(string Value);

public class UserNameTrackingHandler
{
    public static string? ReceivedUserName;

    public void Handle(UserNameTrackingMessage message, Envelope envelope)
    {
        ReceivedUserName = envelope.UserName;
    }
}
