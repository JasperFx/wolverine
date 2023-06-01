using Shouldly;
using Xunit;

namespace PersistenceTests.Marten.MultiTenancy;

public class basic_bootstrapping_and_database_configuration : MultiTenancyContext
{
    public basic_bootstrapping_and_database_configuration(MultiTenancyFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public void bootstrapped_at_all()
    {
        true.ShouldBeTrue();
    }
}