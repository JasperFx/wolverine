using Shouldly;

namespace Wolverine.Http.Tests;

public class non_transactional_attribute_http_tests : IntegrationContext
{
    public non_transactional_attribute_http_tests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void endpoint_with_non_transactional_attribute_should_not_be_transactional()
    {
        var chain = HttpChains.ChainFor("POST", "/non-transactional");
        chain.ShouldNotBeNull();
        chain.IsTransactional.ShouldBeFalse();
    }

    [Fact]
    public void endpoint_without_non_transactional_should_still_be_transactional()
    {
        // The /middleware-messages/{name} endpoint uses [Transactional] explicitly
        var chain = HttpChains.ChainFor("POST", "/middleware-messages/{name}");
        chain.ShouldNotBeNull();
        chain.IsTransactional.ShouldBeTrue();
    }
}
