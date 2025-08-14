using Marten;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class soft_deleted_attribute_usage : IntegrationContext
{
    public soft_deleted_attribute_usage(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task try_it_out()
    {
        // First, a miss
        await Scenario(x =>
        {
            x.Get.Url("/frame-rearrange/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });

        using var session = Host.DocumentStore().LightweightSession();
        var invoice = new Invoice();
        session.Store(invoice);
        await session.SaveChangesAsync();
        
        // second, a hit
        var response = await Scenario(x =>
        {
            x.Get.Url("/frame-rearrange/" + invoice.Id);
        });

        var invoice2 = response.ReadAsJson<Invoice>();
        invoice2.Id.ShouldBe(invoice.Id);
    }
}