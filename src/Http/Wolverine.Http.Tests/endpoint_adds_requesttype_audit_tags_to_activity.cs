using JasperFx.Core;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class endpoint_adds_requesttype_audit_tags_to_activity : IntegrationContext
{
    public endpoint_adds_requesttype_audit_tags_to_activity(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void finds_audit_members_from_attributes()
    {
        var chain = HttpChains.ChainFor("POST", "/auditable/empty");
        
        chain.AuditedMembers.Single()
            .MemberName.ShouldBe(nameof(AuditablePostBody.Id));
    }
    
    [Fact]
    public void adds_auditable_members_to_activity()
    {
        var chain = HttpChains.ChainFor("POST", "/auditable/post");
        var lines = chain.SourceCode.ReadLines();
        lines.Any(x => x.Contains("Activity.Current?.SetTag(\"id\", body.Id)")).ShouldBeTrue();
    }
}