using Shouldly;
using Wolverine.Http.Transport;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

/// <summary>
/// GH-4530. "IWolverineHttpTransportClient is not registered in the service container" named neither the
/// destination nor the fix -- and the interface name is not something the user ever typed, so they could
/// not even grep their own code for it.
/// </summary>
public class no_transport_client_registered_4530
{
    [Fact]
    public void the_message_names_the_destination_the_fix_and_the_docs()
    {
        var message = HttpEndpoint.NoClientRegisteredMessage("https://downstream.example.com/incoming");

        // which destination could not be sent to
        message.ShouldContain("https://downstream.example.com/incoming");

        // the documented fix, keyed on that same destination -- AddHttpClient is named for the URL
        message.ShouldContain("AddHttpClient(\"https://downstream.example.com/incoming\"");

        // ...and the escape hatch, plus somewhere to read more
        message.ShouldContain("IWolverineHttpTransportClient");
        message.ShouldContain("https://wolverinefx.net/guide/http/transport.html");
    }

    [Fact]
    public void the_message_no_longer_leads_with_an_unsearchable_type_name()
    {
        var message = HttpEndpoint.NoClientRegisteredMessage("https://downstream.example.com/incoming");

        // The old text opened with the interface name, which is the one string a user cannot find in
        // their own code. Lead with what they configured instead.
        message.ShouldStartWith("No HTTP transport client is registered for");
    }
}
