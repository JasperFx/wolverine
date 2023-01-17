using System;
using System.Threading.Tasks;
using IntegrationTests;
using Lamar;
using Marten;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PersistenceTests.EFCore;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Codegen;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.SqlServer;
using Xunit;

namespace PersistenceTests;

public class application_of_transaction_middleware : IAsyncLifetime
{
    private IHost _host;
    private HandlerGraph theHandlers;
    private IContainer theContainer;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.Services.AddMarten(Servers.PostgresConnectionString);
            opts.Services.AddDbContextWithWolverineIntegration<SampleDbContext>(x =>
                x.UseSqlServer(Servers.SqlServerConnectionString));
            
            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
            opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
            
            opts.Handlers.AutoApplyTransactions();

            opts.Services.AddScoped<ISomeService, SomeService>();
        }).StartAsync();

        theHandlers = _host.Services.GetRequiredService<IWolverineRuntime>().ShouldBeOfType<WolverineRuntime>()
            .Handlers;

        theContainer = _host.Services.ShouldBeOfType<Container>();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        await theContainer.DisposeAsync();
    }

    [Theory]
    [InlineData(typeof(T1), true)]
    [InlineData(typeof(T2), false)]
    [InlineData(typeof(T3), false)]
    [InlineData(typeof(T4), false)]
    [InlineData(typeof(T5), false)]
    public void sql_server_connection_matching(Type messageType, bool expected)
    {
        var provider = new SqlServerPersistenceFrameProvider();
        var chain = theHandlers.ChainFor(messageType);
        
        provider.CanApply(chain, theContainer).ShouldBe(expected);
    }
    
    [Theory]
    [InlineData(typeof(T1), false)]
    [InlineData(typeof(T2), true)]
    [InlineData(typeof(T3), false)]
    [InlineData(typeof(T4), false)]
    [InlineData(typeof(T5), false)]
    public void postgresql_connection_matching(Type messageType, bool expected)
    {
        var provider = new PostgresqlPersistenceFrameProvider();
        var chain = theHandlers.ChainFor(messageType);
        
        provider.CanApply(chain, theContainer).ShouldBe(expected);
    }
    
    [Theory]
    [InlineData(typeof(T1), false)]
    [InlineData(typeof(T2), false)]
    [InlineData(typeof(T3), true)]
    [InlineData(typeof(T4), false)]
    [InlineData(typeof(T5), true)]
    public void marten_document_session_matching(Type messageType, bool expected)
    {
        var provider = new MartenPersistenceFrameProvider();
        var chain = theHandlers.ChainFor(messageType);
        
        provider.CanApply(chain, theContainer).ShouldBe(expected);
    }
    
    [Theory]
    [InlineData(typeof(T1), false)]
    [InlineData(typeof(T2), false)]
    [InlineData(typeof(T3), false)]
    [InlineData(typeof(T4), true)]
    [InlineData(typeof(T5), false)]
    public void dbcontext_matching(Type messageType, bool expected)
    {
        var provider = new EFCorePersistenceFrameProvider();
        var chain = theHandlers.ChainFor(messageType);

        chain.ShouldNotBeNull();
        
        provider.CanApply(chain, theContainer).ShouldBe(expected);
    }
}

public class T5Handler
{
    private readonly ISomeService _service;

    public T5Handler(ISomeService service)
    {
        _service = service;
    }

    public void Handle(T5 message)
    {
        
    }
}

public interface ISomeService
{
    
}

public class SomeService : ISomeService
{
    public SomeService(IDocumentSession session)
    {
    }
}

public static class TransactionHandler
{
    public static void Handle(T1 t, SqlConnection connection)
    {
        
    }
    
    public static void Handle(T2 t, NpgsqlTransaction connection)
    {
        
    }
    
    public static void Handle(T3 t, IDocumentSession session)
    {
        
    }

    public static void Handle(T4 t, SampleDbContext context)
    {
        
    }
}

public record T1;
public record T2;
public record T3;
public record T4;
public record T5;