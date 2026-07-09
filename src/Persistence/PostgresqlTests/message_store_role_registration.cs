using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Xunit;

namespace PostgresqlTests;

public class message_store_role_registration
{
    private static IWolverineRuntime buildRuntime()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(new WolverineOptions());
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.DurabilitySettings.Returns(new DurabilitySettings());
        runtime.Services.Returns(new ServiceCollection().BuildServiceProvider());

        return runtime;
    }

    [Fact]
    public void ancillary_role_is_honored_for_a_single_database()
    {
        var options = new WolverineOptions();
        var persistence = (PostgresqlBackedPersistence)options.PersistMessagesWithPostgresql(
            Servers.PostgresConnectionString, "wolverine", MessageStoreRole.Ancillary);

        persistence.BuildMessageStore(buildRuntime())
            .Role.ShouldBe(MessageStoreRole.Ancillary);
    }

    [Fact]
    public void ancillary_role_is_honored_with_statically_registered_tenants()
    {
        var options = new WolverineOptions();
        var persistence = (PostgresqlBackedPersistence)options
            .PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine", MessageStoreRole.Ancillary)
            .RegisterStaticTenants(tenants => tenants.Register("one", Servers.PostgresConnectionString));

        var store = persistence.BuildMessageStore(buildRuntime()).ShouldBeOfType<MultiTenantedMessageStore>();

        // The composite store itself is always Composite. The role that MessageStoreCollection
        // keys off of lives on the inner, main database
        store.Main.Role.ShouldBe(MessageStoreRole.Ancillary);
    }

    [Fact]
    public void main_is_still_the_default_role_with_statically_registered_tenants()
    {
        var options = new WolverineOptions();
        var persistence = (PostgresqlBackedPersistence)options
            .PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine")
            .RegisterStaticTenants(tenants => tenants.Register("one", Servers.PostgresConnectionString));

        var store = persistence.BuildMessageStore(buildRuntime()).ShouldBeOfType<MultiTenantedMessageStore>();

        store.Main.Role.ShouldBe(MessageStoreRole.Main);
    }
}
