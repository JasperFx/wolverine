using Marten;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Http;
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
      x.Post.Json(new CreateIssue("Title")).ToUrl("/issue");
      x.StatusCodeShouldBe(201);
    });

    var issue = result.ReadAsJson<IssueCreated>();
    
    // This will throw a silent error, unsure how to test
    await Scenario(x =>
    {
      x.Get.Url("/write-to/" + issue.Id);
      x.StatusCodeShouldBe(200);
    });
  }
}
