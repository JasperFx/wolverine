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
using Wolverine.SqlServer;


namespace EfCoreTests;

public abstract class Fixture
{
    public record AbstractionCommand;

    public interface IRepository;
    
    public interface IUnitOfWork
    {
        IRepository GetRepository();
        Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    }

    public class AbstractionDbContext(DbContextOptions<AbstractionDbContext> options) : DbContext(options), IUnitOfWork, IRepository
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.MapWolverineEnvelopeStorage("wolverine");
        }

        IRepository IUnitOfWork.GetRepository() => this;
    }

    [Transactional]
    [WolverineHandler]
    public class AbstractionCommandHandler
    {
        public Task Handle(AbstractionCommand command, IUnitOfWork uow)
        {
            var repository = uow.GetRepository();

            // Happily use abstract repositories

            return Task.CompletedTask;
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
                opts.Services.AddDbContextWithWolverineIntegration<Fixture.AbstractionDbContext>(x =>
                    x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.Services.AddScoped<Fixture.IUnitOfWork, Fixture.AbstractionDbContext>();

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "txmode");

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<Fixture.IUnitOfWork, Fixture.AbstractionDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<Fixture.AbstractionCommandHandler>();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var chain = runtime.Handlers.ChainFor<Fixture.AbstractionCommand>();

        chain.ShouldNotBeNull();
        chain.Middleware.OfType<EnrollDbContextInTransaction>().ShouldNotBeEmpty();
        chain.Middleware.OfType<EFCorePersistenceFrameProvider.CastDbContextFrame>().ShouldNotBeEmpty();
    }


    [Fact]
    public async Task execution_works_with_abstraction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContext<Fixture.AbstractionDbContext>(x => x.UseInMemoryDatabase("abstraction_exec"));
                opts.Services.AddScoped<Fixture.IUnitOfWork, Fixture.AbstractionDbContext>();

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<Fixture.IUnitOfWork, Fixture.AbstractionDbContext>();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<Fixture.AbstractionCommandHandler>();
            }).StartAsync();

        // If it compiles and runs without error, the cast worked
        Should.NotThrow(async () => await host.InvokeMessageAndWaitAsync(new Fixture.AbstractionCommand()));
    }

    [Fact]
    public async Task should_add_save_changes_async_call_to_postprocessors()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddDbContext<Fixture.AbstractionDbContext>(x => x.UseInMemoryDatabase("abstraction_post"));
                opts.Services.AddScoped<Fixture.IUnitOfWork, Fixture.AbstractionDbContext>();

                opts.UseEntityFrameworkCoreTransactions()
                    .WithDbContextAbstraction<Fixture.IUnitOfWork, Fixture.AbstractionDbContext>();

                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<Fixture.AbstractionCommandHandler>();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var chain = runtime.Handlers.ChainFor<Fixture.AbstractionCommand>();

        chain.ShouldNotBeNull();
        chain.Postprocessors.OfType<MethodCall>()
            .Any(x => x.Method.Name == nameof(DbContext.SaveChangesAsync))
            .ShouldBeTrue();
    }
}
