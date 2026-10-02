using JasperFx.Descriptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace EfCoreTests.MultiTenancy;

public class tenanted_db_context_builders_report_a_tenant_without_a_database
{
    private const string TenantId = "blank";

    private static MultiTenantedMessageStore storeWithBlankTenant()
    {
        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);

        var database = Substitute.For<IMessageDatabase>();
        database.Settings.Returns(new DatabaseSettings());

        var source = Substitute.For<ITenantedMessageSource>();
        source.FindAsync(TenantId).Returns(new ValueTask<IMessageStore>(database));

        return new MultiTenantedMessageStore(Substitute.For<IMessageStore>(), runtime, source);
    }

    [Fact]
    public async Task by_connection_string()
    {
        var builder = new TenantedDbContextBuilderByConnectionString<ItemsDbContext>(Substitute.For<IServiceProvider>(),
            storeWithBlankTenant(), (_, _, _) => { }, []);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(TenantId, CancellationToken.None));

        ex.Message.ShouldBe($"Unable to find a database connection string for tenant '{TenantId}'");
    }

    [Fact]
    public async Task by_db_data_source()
    {
        var builder = new TenantedDbContextBuilderByDbDataSource<ItemsDbContext>(Substitute.For<IServiceProvider>(),
            storeWithBlankTenant(), (_, _, _) => { }, []);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await builder.BuildAsync(TenantId, CancellationToken.None));

        ex.Message.ShouldBe($"Unable to find a DbDataSource for tenant '{TenantId}'");
    }
}
