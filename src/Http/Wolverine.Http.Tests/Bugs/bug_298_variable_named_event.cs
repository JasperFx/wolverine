using Shouldly;
using WolverineWebApi.Bugs;

namespace Wolverine.Http.Tests.Bugs;

public class bug_298_variable_named_event : IntegrationContext
{
    public bug_298_variable_named_event(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task do_not_blow_up()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new TelegramUpdated("foo")).ToUrl("/convert-book");
            x.StatusCodeShouldBe(204);
        });

        tracked.Executed.SingleMessage<TelegramUpdated>()
            .Name.ShouldBe("foo");
    }
}