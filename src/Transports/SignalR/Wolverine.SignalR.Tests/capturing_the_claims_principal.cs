using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Shouldly;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR.Tests;

// GH-2927: SignalREnvelope captures the connection's ClaimsPrincipal (HubCallerContext.User) so
// Wolverine handlers/middleware can perform per-message authorization from the principal's claims.
public class capturing_the_claims_principal
{
    [Fact]
    public void captures_principal_connection_id_and_user_name_from_the_hub_context()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "wolverine"), new Claim(ClaimTypes.Role, "admin")], "test"));

        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("conn-1");
        context.UserIdentifier.Returns("wolverine");
        context.User.Returns(principal);

        var envelope = new SignalREnvelope(context, Substitute.For<IHubContext<Hub>>());

        envelope.Principal.ShouldBeSameAs(principal);
        envelope.Principal!.IsInRole("admin").ShouldBeTrue();
        envelope.ConnectionId.ShouldBe("conn-1");
        envelope.UserName.ShouldBe("wolverine");
    }

    [Fact]
    public void principal_is_null_for_an_unauthenticated_connection()
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("conn-2");
        context.User.Returns((ClaimsPrincipal?)null);

        var envelope = new SignalREnvelope(context, Substitute.For<IHubContext<Hub>>());

        envelope.Principal.ShouldBeNull();
    }
}
