using IntegrationTests;
using JasperFx.CodeGeneration.Frames;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Tracking;
using Wolverine.Postgresql;
using JasperFx.Resources;


namespace EfCoreTests;

public abstract class DbContextAbstractionTestFixture
{
    public record AbstractionCommand;

    public interface IItemRepository;

    public class AbstractionDbContext(DbContextOptions<AbstractionDbContext> options) : DbContext(options), IItemRepository
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.MapWolverineEnvelopeStorage("wolverine");
        }
    }

    [WolverineHandler]
    public class AbstractionCommandHandler
    {
        public async Task Handle(AbstractionCommand command, IItemRepository items)
        {

        }
    }
}

public class dbContext_transactions_with_abstractions_tests
{
    [Fact]
    public async Task can_apply_transactional_middleware_to_abstraction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.CodeGeneration.SourceCodeWritingEnabled = true;
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddDbContextWithWolverineIntegration<DbContextAbstractionTestFixture.AbstractionDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.Services.AddScoped<DbContextAbstractionTestFixture.IItemRepository, DbContextAbstractionTestFixture.AbstractionDbContext>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");

                opts.UseEntityFrameworkCoreTransactions(mode: Wolverine.Persistence.TransactionMiddlewareMode.Eager)
                    .WithDbContextAbstraction<DbContextAbstractionTestFixture.IItemRepository, DbContextAbstractionTestFixture.AbstractionDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<DbContextAbstractionTestFixture.AbstractionCommandHandler>();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var chain = runtime.Handlers.ChainFor<DbContextAbstractionTestFixture.AbstractionCommand>();

        chain.ShouldNotBeNull();

        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();
        chain.Middleware.OfType<EFCorePersistenceFrameProvider.CastDbContextFrame>().ShouldNotBeEmpty();
    }


    [Fact]
    public async Task codegen_works_with_abstraction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<DbContextAbstractionTestFixture.AbstractionDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.Services.AddScoped<DbContextAbstractionTestFixture.IItemRepository, DbContextAbstractionTestFixture.AbstractionDbContext>();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<DbContextAbstractionTestFixture.IItemRepository, DbContextAbstractionTestFixture.AbstractionDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<DbContextAbstractionTestFixture.AbstractionCommandHandler>();
            }).StartAsync();

        // If it compiles and runs without error, the cast worked
        Should.NotThrow(async () => await host.InvokeMessageAndWaitAsync(new DbContextAbstractionTestFixture.AbstractionCommand()));
    }

    [Fact]
    public async Task should_add_save_changes_async_call_to_postprocessors()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContextWithWolverineIntegration<DbContextAbstractionTestFixture.AbstractionDbContext>(x =>
                    x.UseNpgsql(Servers.PostgresConnectionString));

                opts.Services.AddScoped<DbContextAbstractionTestFixture.IItemRepository, DbContextAbstractionTestFixture.AbstractionDbContext>();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "wolverine");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<DbContextAbstractionTestFixture.IItemRepository, DbContextAbstractionTestFixture.AbstractionDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<DbContextAbstractionTestFixture.AbstractionCommandHandler>();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var chain = runtime.Handlers.ChainFor<DbContextAbstractionTestFixture.AbstractionCommand>();

        chain.ShouldNotBeNull();
        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }
}
