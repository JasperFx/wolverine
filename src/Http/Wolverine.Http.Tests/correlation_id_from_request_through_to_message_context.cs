using Shouldly;
using Wolverine.Http.Runtime;

namespace Wolverine.Http.Tests;

public class correlation_id_from_request_through_to_message_context : IntegrationContext
{
    public correlation_id_from_request_through_to_message_context(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task trace_the_correlation_id()
    {
        var id = Guid.NewGuid().ToString();

        var body = await Scenario(x =>
        {
            x.Get.Url("/correlation");
            x.WithRequestHeader(RequestIdMiddleware.CorrelationIdHeaderKey, id);
        });
        
        body.ReadAsText().ShouldBe(id);
    }

}