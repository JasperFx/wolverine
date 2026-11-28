using JasperFx.Events.EventModeling;
using Microsoft.AspNetCore.Authorization;
using Shouldly;
using Wolverine.Http.Diagnostics;

namespace Wolverine.Http.Tests;

// GH-6005 (jasperfx#1298): the HTTP source fills the slice's Actor with the roles the endpoint's authorization
// metadata requires -- [Authorize(Roles = "...")] -- and nothing else, because nothing else about who calls
// a route is in the code. A policy name is not a role; [AllowAnonymous] requires none.
public class event_model_actor_roles_6005
{
    [Fact]
    public void authorize_roles_become_the_required_roles_of_the_actor()
    {
        var chain = HttpChain.ChainFor<GuardedEndpoints>(x => x.Approve(null!));

        HttpEventModelSource.RequiredRolesOf(chain).ShouldBe(["claims-adjuster", "supervisor"]);

        var slice = HttpEventModelSource.ForChain(chain);
        slice.Actor.ShouldNotBeNull();
        slice.Actor.RequiredRoles.ShouldBe(["claims-adjuster", "supervisor"]);
        slice.Actor.Name.ShouldBe("claims-adjuster, supervisor");
        slice.Claims(EventModelRole.Actor).ShouldBeTrue();
    }

    [Fact]
    public void an_authorize_without_roles_or_with_only_a_policy_claims_no_actor()
    {
        HttpEventModelSource.ForChain(HttpChain.ChainFor<GuardedEndpoints>(x => x.Audit(null!))).Actor.ShouldBeNull();
        HttpEventModelSource.ForChain(HttpChain.ChainFor<GuardedEndpoints>(x => x.Reject(null!))).Actor.ShouldBeNull();
    }

    [Fact]
    public void roles_on_the_class_apply_to_its_endpoints_unless_the_endpoint_allows_anonymous()
    {
        HttpEventModelSource.RequiredRolesOf(HttpChain.ChainFor<SupervisedEndpoints>(x => x.Escalate(null!)))
            .ShouldBe(["supervisor"]);
        HttpEventModelSource.RequiredRolesOf(HttpChain.ChainFor<SupervisedEndpoints>(x => x.Peek(null!)))
            .ShouldBeEmpty();
    }
}

public record ApproveClaim(Guid ClaimId);
public record AuditClaim(Guid ClaimId);
public record RejectClaim(Guid ClaimId);
public record EscalateClaim(Guid ClaimId);
public record PeekAtClaim(Guid ClaimId);

public class GuardedEndpoints
{
    [WolverinePost("/claims/approve")]
    [Authorize(Roles = "claims-adjuster, supervisor")]
    public string Approve(ApproveClaim command) => "approved";

    [WolverinePost("/claims/audit")]
    [Authorize]
    public string Audit(AuditClaim command) => "audited";

    [WolverinePost("/claims/reject")]
    [Authorize(Policy = "claims")]
    public string Reject(RejectClaim command) => "rejected";
}

[Authorize(Roles = "supervisor")]
public class SupervisedEndpoints
{
    [WolverinePost("/claims/escalate")]
    public string Escalate(EscalateClaim command) => "escalated";

    [WolverinePost("/claims/peek")]
    [AllowAnonymous]
    public string Peek(PeekAtClaim command) => "peeked";
}
