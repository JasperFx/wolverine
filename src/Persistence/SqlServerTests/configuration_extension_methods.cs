using IntegrationTests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;

namespace SqlServerTests;

public class configuration_extension_methods : SqlServerContext
{
    [Fact]
    public void bootstrap_with_configuration()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                    { { "connection", Servers.SqlServerConnectionString } });
            })
            .UseWolverine((context, options) =>
            {
                options.PersistMessagesWithSqlServer(context.Configuration["connection"]);
            });

        using var host = builder.Build();
        host.Services.GetRequiredService<SqlServerMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.SqlServerConnectionString);

        var databases = host.Services.GetServices<IDatabase>();
        var store = databases.OfType<SqlServerMessageStore>().Single();

        // only one, so should be master
        store.Settings.IsMaster.ShouldBeTrue();
    }

    [Fact]
    public void bootstrap_with_connection_string()
    {
        using var runtime = WolverineHost.For(x =>
            x.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString));
        runtime.Get<SqlServerMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.SqlServerConnectionString);
    }
}