using Marten.AspNetCore;
using Microsoft.AspNetCore.Http;
using Shouldly;
using WolverineWebApi;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class using_write_by_id : IntegrationContext
{
    public using_write_by_id(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task use_marten_write_to()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new CreateIssue("Some Title")).ToUrl("/issue");
            x.StatusCodeShouldBe(201);
        });

        var issue = result.ReadAsJson<IssueCreated>();

        // This will throw a silent error, unsure how to test
        result = await Scenario(x =>
        {
            x.Get.Url("/write-to/" + issue.Id);
            x.StatusCodeShouldBe(200);
        });

        var entity = result.ReadAsJson<Issue>();
        entity.Id.ShouldBe(issue.Id);
        entity.Title.ShouldBe("Some Title");
    }
}