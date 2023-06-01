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

    [Fact]
    public void should_have_the_specified_master_database_as_master()
    {
        throw new NotImplementedException();
    }

    [Fact]
    public void knows_about_tenant_databases()
    {
        throw new NotImplementedException();
    }

    [Fact]
    public async Task tenant_databases_have_envelope_tables()
    {
        throw new NotImplementedException();
    }

    [Fact]
    public async Task tenant_databases_do_not_have_node_and_assignment_tables()
    {
        throw new NotImplementedException();
    }

    [Fact]
    public async Task master_database_has_node_assignment_and_control_queue_tables()
    {
        throw new NotImplementedException();
    }
}