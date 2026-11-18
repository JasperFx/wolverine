using JasperFx.Core;
using Wolverine.ComplianceTests;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Transports.Tcp;

public class message_forwarding
{
    [Fact]
    public async Task send_message_via_forwarding()
    {
        // Not a fixed port. Several other suites (PostgresqlTests, SqliteTests, OracleTests...) listen at 2345, and
        // the local gate runs suites side by side. When another process held 2345 this listener failed to bind
        // without failing the host, the send went to the other process, and the tracked session timed out.
        var port = PortFinder.GetAvailablePort();

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.DisableConventionalDiscovery();
            opts.IncludeType<NewMessageHandler>();

            // 6.0: forwarders are no longer auto-discovered from the application
            // assembly — register the IForwardsTo pair explicitly. See #2757.
            opts.RegisterMessageForwarder<OriginalMessage, NewMessage>();

            opts.Publish(x =>
            {
                x.Message<OriginalMessage>();
                x.ToPort(port);
            });

            opts.ListenAtPort(port);
        });

        var originalMessage = new OriginalMessage { FirstName = "James", LastName = "Worthy" };

        var session = await host
            .TrackActivity()
            .IncludeExternalTransports()
            .ExecuteAndWaitAsync(c => c.EndpointFor($"tcp://localhost:{port}".ToUri()).SendAsync( originalMessage));


        session.FindSingleTrackedMessageOfType<NewMessage>(MessageEventType.MessageSucceeded)
            .FullName.ShouldBe("James Worthy");
    }
}

[MessageIdentity("versioned-message", Version = 1)]
public class OriginalMessage : IForwardsTo<NewMessage>
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;

    public NewMessage Transform()
    {
        return new NewMessage { FullName = $"{FirstName} {LastName}" };
    }
}

[MessageIdentity("versioned-message", Version = 2)]
public class NewMessage
{
    public string FullName { get; set; } = null!;
}

public class NewMessageHandler
{
    public void Handle(NewMessage message)
    {
    }
}