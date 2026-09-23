using System.Net;
using Shouldly;
using Wolverine.SignalR.Client;
using Xunit;

namespace Wolverine.SignalR.Tests;

/// <summary>
/// GH-4519: a 401 on StartAsync() used to be logged and swallowed behind a `//throw;` FIXME.
/// WithAutomaticReconnect() only engages after a successful initial connection, so the endpoint
/// stayed dead forever and every later send failed with "SignalR Client {Uri} is not initialized" --
/// which blames startup ordering for an authorization problem.
/// </summary>
public class client_connect_failure_4519
{
    private static readonly Uri TheHub = new("https://example.com/hub");

    [Fact]
    public void a_401_names_the_access_token_provider_and_the_hub_policy()
    {
        var message = SignalRClientEndpoint.ConnectFailureMessage(TheHub, HttpStatusCode.Unauthorized);

        message.ShouldContain("https://example.com/hub");
        message.ShouldContain("401 Unauthorized");
        message.ShouldContain("AccessTokenProvider");
        message.ShouldContain("authorization policy");
    }

    [Fact]
    public void a_403_says_the_token_was_accepted_but_insufficient()
    {
        var message = SignalRClientEndpoint.ConnectFailureMessage(TheHub, HttpStatusCode.Forbidden);

        message.ShouldContain("403 Forbidden");
        message.ShouldContain("does not satisfy");
    }

    [Fact]
    public void a_transport_level_failure_points_at_the_uri_dns_and_tls()
    {
        // HttpRequestException.StatusCode is null when the request never reached a response --
        // DNS, connection refused, a TLS handshake failure.
        var message = SignalRClientEndpoint.ConnectFailureMessage(TheHub, null);

        message.ShouldContain("before the hub responded");
        message.ShouldContain("DNS");
        message.ShouldContain("TLS");
    }

    [Fact]
    public void any_other_status_code_is_still_reported_by_number()
    {
        var message = SignalRClientEndpoint.ConnectFailureMessage(TheHub, HttpStatusCode.ServiceUnavailable);

        message.ShouldContain("503");
        message.ShouldContain("https://example.com/hub");
    }
}
