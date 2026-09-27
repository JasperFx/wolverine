using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests;

/// <summary>
///     GH-4635. Two DbContext registration shapes EF Core documents and Wolverine had never met.
/// </summary>
/// <remarks>
///     <para>
///     <b><c>IDbContextFactory&lt;T&gt;</c> / <c>AddDbContextFactory</c> / <c>PooledDbContextFactory</c>.</b>
///     <c>EFCorePersistenceFrameProvider.CanApply</c> looks for a chain dependency that
///     <c>CanBeCastTo&lt;DbContext&gt;()</c>. A factory is not one, so no EF middleware applied at all and
///     the handler got no transaction, no <c>SaveChangesAsync</c> and no outbox — silently, with a fully
///     compiling handler and a green test suite. The issue's instruction was "either support or fail
///     loudly"; supporting it is a design change, so this asserts the loud failure.
///     </para>
///     <para>
///     <b><c>AddDbContextPool&lt;T&gt;</c>.</b> Different problem, and not silent in the same way: pooling
///     DOES register <c>T</c> as scoped, so the middleware applies. What it does not do is run
///     <c>WolverineModelCustomizer</c>, so the envelope tables are absent from the model and the outbox
///     falls back to the raw ADO path. That is a supported configuration (see
///     <c>eager_idempotency_with_non_wolverine_mapped_db_context</c>) and the test below pins it as
///     working rather than refusing it.
///     </para>
/// </remarks>
[Collection("sqlserver")]
public class a_handler_injecting_a_dbcontext_factory
{
    private static async Task<IHost> startWith(Type handlerType)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);

                // The shape under test: ONLY the factory is registered. AddDbContextFactory does not
                // register the DbContext itself.
                opts.Services.AddDbContextFactory<FactoryTodoDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dbcontext_factory");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.CodeGeneration.AlwaysUseServiceLocationFor<IDbContextFactory<FactoryTodoDbContext>>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Before the diagnostic existed this host started cleanly and the handler ran with no
    ///     transaction and no outbox — nothing anywhere said so. Red for exactly that reason.
    /// </summary>
    [Fact]
    public async Task fails_loudly_instead_of_silently_skipping_the_transaction()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await startWith(typeof(FactoryInjectingHandler));
        });

        ex.Message.ShouldContain("IDbContextFactory<EfCoreTests.FactoryTodoDbContext>");
        ex.Message.ShouldContain("no transaction");
        ex.Message.ShouldContain("outbox");
        // The two ways out, both named
        ex.Message.ShouldContain("FactoryTodoDbContext");
        ex.Message.ShouldContain("[NonTransactional]");
    }

    /// <summary>
    ///     The escape hatch has to actually work, or the diagnostic is just a wall. <c>[NonTransactional]</c>
    ///     already means "I am not asking Wolverine to manage this handler's transaction", which is exactly
    ///     the acknowledgement being demanded.
    /// </summary>
    [Fact]
    public async Task non_transactional_opts_out_of_the_refusal()
    {
        using var host = await startWith(typeof(NonTransactionalFactoryHandler));

        await host.InvokeMessageAndWaitAsync(new UseFactoryDeliberately());
    }

    /// <summary>
    ///     A handler that takes BOTH the factory and the DbContext is not the silent case: the middleware
    ///     applies to the injected DbContext, so the handler has a transaction and an outbox and the
    ///     factory is an explicit second connection. No refusal.
    /// </summary>
    [Fact]
    public async Task a_chain_that_also_injects_the_dbcontext_is_left_alone()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(FactoryAndContextHandler));

                opts.Services.AddDbContextWithWolverineIntegration<FactoryTodoDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString), "dbcontext_factory");
                opts.Services.AddDbContextFactory<FactoryTodoDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "dbcontext_factory");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();

                opts.CodeGeneration.AlwaysUseServiceLocationFor<IDbContextFactory<FactoryTodoDbContext>>();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        host.ShouldNotBeNull();
    }
}

/// <summary>
///     GH-4635. <c>AddDbContextPool&lt;T&gt;</c>. Pinning what a pooled context actually does today, which
///     is the "not Wolverine-mapped, raw ADO path" the issue describes — and which works.
/// </summary>
[Collection("sqlserver")]
public class a_pooled_dbcontext : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        // The table is created with raw ADO rather than through
        // UseEntityFrameworkCoreWolverineManagedMigrations() on purpose -- see
        // the_resource_setup_path_poisons_the_pool below.
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await conn.CreateCommand(
                    """
                    IF SCHEMA_ID('pooled_dbcontext') IS NULL EXEC('CREATE SCHEMA pooled_dbcontext');
                    IF OBJECT_ID('pooled_dbcontext.pooled_todos') IS NULL
                        CREATE TABLE pooled_dbcontext.pooled_todos (Id nvarchar(450) NOT NULL PRIMARY KEY, Name nvarchar(max) NULL);
                    """)
                .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PooledTodoHandler));

                // Deliberately NOT AddDbContextWithWolverineIntegration: pooling goes through EF's own
                // registration, which never runs WolverineModelCustomizer.
                opts.Services.AddDbContextPool<PooledTodoDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "pooled_dbcontext");
                opts.UseEntityFrameworkCoreTransactions();
                opts.Policies.AutoApplyTransactions();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    ///     The envelope tables are NOT in a pooled context's model, which is what sends the outbox down
    ///     the raw ADO path rather than through EF. Asserted directly so the day pooling does get wired
    ///     into <c>WolverineModelCustomizer</c>, this test says so.
    /// </summary>
    [Fact]
    public void the_pooled_context_is_not_wolverine_mapped()
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PooledTodoDbContext>();

        db.Model.GetEntityTypes()
            .Any(x => x.ClrType.Namespace?.StartsWith("Wolverine") == true)
            .ShouldBeFalse();
    }

    /// <summary>
    ///     ...and it still works end to end: the handler's write commits and the cascading message is
    ///     delivered. The middleware applies because <c>AddDbContextPool</c> registers the context type
    ///     itself, which is the difference from the factory case above.
    /// </summary>
    [Fact]
    public async Task writes_and_cascades_through_the_raw_ado_outbox_path()
    {
        var id = Guid.NewGuid().ToString();

        var tracked = await _host.InvokeMessageAndWaitAsync(new CreatePooledTodo(id, "Pooled"));

        tracked.Sent.SingleMessage<PooledTodoCreated>().Id.ShouldBe(id);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PooledTodoDbContext>();
        (await db.PooledTodos.FindAsync([id], TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Name.ShouldBe("Pooled");
    }

    /// <summary>
    ///     GH-4635, and the reason the fixture above builds its table with raw ADO. Add
    ///     <c>UseEntityFrameworkCoreWolverineManagedMigrations()</c> — which is what the EF Core guide tells
    ///     you to do, and what every other EF suite in this project does — to the SAME pooled registration
    ///     and the first handler invocation dies opening a connection.
    /// </summary>
    /// <remarks>
    ///     <b>Pinned, not fixed.</b> <c>EntityFrameworkCoreSystemPart</c> resolves every registered
    ///     <c>DbContext</c> out of a scope and hands it to Weasel's <c>CreateDatabase</c>, which takes over
    ///     the context's <c>DbConnection</c>. With <c>AddDbContext</c> that context is thrown away at the end
    ///     of the scope; with <c>AddDbContextPool</c> it goes back into the pool, and the next lease out of
    ///     that pool has no connection string. So the combination of pooling and Wolverine-managed
    ///     migrations — neither of which is exotic — produces a host that starts clean and then fails on
    ///     every message. This is the concrete shape behind the issue's "a pooled context is not
    ///     Wolverine-mapped and takes the raw ADO path"; the raw ADO path itself is fine (see above), the
    ///     resource-setup path is not.
    /// </remarks>
    [Fact]
    public async Task the_resource_setup_path_poisons_the_pool()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(PooledTodoHandler));

                opts.Services.AddDbContextPool<PooledTodoDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "pooled_dbcontext");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await host.InvokeMessageAndWaitAsync(new CreatePooledTodo(Guid.NewGuid().ToString(), "Poisoned")));

        ex.Message.ShouldContain("ConnectionString");
    }
}

public class FactoryTodo
{
    public string Id { get; set; } = null!;
    public string? Name { get; set; }
}

public class FactoryTodoDbContext : DbContext
{
    public FactoryTodoDbContext(DbContextOptions<FactoryTodoDbContext> options) : base(options)
    {
    }

    public DbSet<FactoryTodo> FactoryTodos { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FactoryTodo>(map =>
        {
            map.ToTable("factory_todos", "dbcontext_factory");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

public record UseFactoryDeliberately;

[WolverineIgnore]
public static class FactoryInjectingHandler
{
    public static async Task Handle(UseFactoryDeliberately command, IDbContextFactory<FactoryTodoDbContext> factory,
        CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.SaveChangesAsync(token);
    }
}

[WolverineIgnore]
[NonTransactional]
public static class NonTransactionalFactoryHandler
{
    public static async Task Handle(UseFactoryDeliberately command, IDbContextFactory<FactoryTodoDbContext> factory,
        CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.SaveChangesAsync(token);
    }
}

[WolverineIgnore]
public static class FactoryAndContextHandler
{
    public static async Task Handle(UseFactoryDeliberately command, FactoryTodoDbContext db,
        IDbContextFactory<FactoryTodoDbContext> factory, CancellationToken token)
    {
        await using var other = await factory.CreateDbContextAsync(token);
        await db.SaveChangesAsync(token);
    }
}

public class PooledTodo
{
    public string Id { get; set; } = null!;
    public string? Name { get; set; }
}

public class PooledTodoDbContext : DbContext
{
    public PooledTodoDbContext(DbContextOptions<PooledTodoDbContext> options) : base(options)
    {
    }

    public DbSet<PooledTodo> PooledTodos { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PooledTodo>(map =>
        {
            map.ToTable("pooled_todos", "pooled_dbcontext");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
        });
    }
}

public record CreatePooledTodo(string Id, string Name);

public record PooledTodoCreated(string Id);

[WolverineIgnore]
public static class PooledTodoHandler
{
    public static async Task<PooledTodoCreated> Handle(CreatePooledTodo command, PooledTodoDbContext db,
        CancellationToken token)
    {
        await db.PooledTodos.AddAsync(new PooledTodo { Id = command.Id, Name = command.Name }, token);
        return new PooledTodoCreated(command.Id);
    }

    public static void Handle(PooledTodoCreated created)
    {
    }
}
