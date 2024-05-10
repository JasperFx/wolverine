using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
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
}