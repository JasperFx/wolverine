using IntegrationTests;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

namespace PersistenceTests;

public class configuration_extension_methods : PostgresqlContext
{
    [Fact]
    public void bootstrap_with_configuration()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                    { { "connection", Servers.PostgresConnectionString } });
            })
            .UseWolverine((context, x) => { x.PersistMessagesWithPostgresql(context.Configuration["connection"]); });


        using var host = builder.Build();
        host.Services.As<IContainer>().GetInstance<IMessageStore>().ShouldBeOfType<PostgresqlMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.PostgresConnectionString);

        
        var store = host.Services.GetServices<IDatabase>().OfType<PostgresqlMessageStore>().Single();
        
        // Only one, so should be master
        store.Settings.IsMaster.ShouldBeTrue();
        
    }


    [Fact]
    public void bootstrap_with_connection_string()
    {
        using var host = WolverineHost.For(x =>
            x.PersistMessagesWithPostgresql(Servers.PostgresConnectionString));
        host.Services.As<IContainer>().GetInstance<IMessageStore>().ShouldBeOfType<PostgresqlMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.PostgresConnectionString);

    }
}