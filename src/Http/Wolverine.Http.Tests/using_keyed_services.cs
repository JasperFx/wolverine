namespace Wolverine.Http.Tests;

public class using_keyed_services : IntegrationContext
{
    public using_keyed_services(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task using_singleton_service()
    {
        await Scenario(x => x.Get.Url("/thing/red"));
    }
    
    [Fact]
    public async Task using_scoped_service()
    {
        await Scenario(x => x.Get.Url("/thing/blue"));
    }
}