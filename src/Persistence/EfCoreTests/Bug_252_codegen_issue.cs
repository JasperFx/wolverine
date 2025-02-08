using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime.Handlers;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit.Abstractions;

namespace EfCoreTests;

[Collection("sqlserver")]
public class Bug_252_codegen_issue
{
    private readonly ITestOutputHelper _output;

    public Bug_252_codegen_issue(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task use_the_saga_type_to_determine_the_correct_DbContext_type()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opt =>
            {
                opt.Services.AddDbContextWithWolverineIntegration<AppDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opt.Services.AddScoped<IOrderRepository, OrderRepository>();

                opt.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opt.UseEntityFrameworkCoreTransactions();
                opt.Policies.UseDurableLocalQueues();
                opt.Policies.AutoApplyTransactions();
            }).StartAsync();

        var table = new Table("OrderSagas");
        table.AddColumn<Guid>("id").AsPrimaryKey();
        table.AddColumn<int>("version");
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, table);
        await new SqlServerMigrator().ApplyAllAsync(conn, migration, AutoCreate.All);

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        await conn.CloseAsync();

        await host.InvokeMessageAndWaitAsync(new OrderCreated(Guid.NewGuid()));
    }

    [Fact]
    public async Task bug_256_message_bus_should_be_in_outbox_transaction()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opt =>
            {
                opt.Services.AddDbContextWithWolverineIntegration<AppDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opt.Services.AddScoped<IOrderRepository, OrderRepository>();

                opt.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
                opt.UseEntityFrameworkCoreTransactions();
                opt.Policies.UseDurableLocalQueues();
                opt.Policies.AutoApplyTransactions();
            }).StartAsync();

        var table = new Table("OrderSagas");
        table.AddColumn<Guid>("id").AsPrimaryKey();
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync();

        var migration = await SchemaMigration.DetermineAsync(conn, table);
        await new SqlServerMigrator().ApplyAllAsync(conn, migration, AutoCreate.All);

        await conn.CloseAsync();

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        var chain = host.Services.GetRequiredService<HandlerGraph>().HandlerFor<CreateOrder>().As<MessageHandler>().Chain;
        
        var lines = chain.SourceCode.ReadLines();

        // Just proving that the code generation did NOT opt to use a nested container
        // for creating the handler
        lines.Any(x =>
                x.Contains(
                    "var createOrderHandler = new EfCoreTests.CreateOrderHandler(orderRepository, context);"))
            .ShouldBeTrue();
    }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<ProcessOrderSaga> OrderSagas { get; set; }
}

public record CreateOrder(Guid Id);

public record OrderCreated(Guid Id);

public class ProcessOrderSaga : Saga
{
    public Guid Id { get; set; }

    public void Start(OrderCreated order)
    {
        Id = order.Id;
    }
}

public interface IOrderRepository
{
    void Add(ProcessOrderSaga saga);
}

public class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _dbContext;

    public OrderRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [Transactional]
    public void Add(ProcessOrderSaga saga)
    {
        _dbContext.OrderSagas.Add(saga);
    }
}

public class CreateOrderHandler
{
    private readonly IMessageBus _messageBus;
    private readonly IOrderRepository _repository;

    public CreateOrderHandler(IOrderRepository repository, IMessageBus messageBus)
    {
        _repository = repository;
        _messageBus = messageBus;
    }

    public ValueTask HandleAsync(CreateOrder command)
    {
        _repository.Add(new ProcessOrderSaga { Id = command.Id });
        return _messageBus.PublishAsync(new OrderCreated(command.Id));
    }
}