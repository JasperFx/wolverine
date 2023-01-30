using Alba;
using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class using_services_in_method : IntegrationContext
{
    public using_services_in_method(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task using_service_with_custom_variable_source()
    {
        var data = new Data { Name = "foo" };

        using (var session = Store.LightweightSession())
        {
            session.Store(data);
            await session.SaveChangesAsync();
        }

        var result = await Host.GetAsJson<Data>($"/data/{data.Id}");
        
        result.Name.ShouldBe("foo");
    }
}