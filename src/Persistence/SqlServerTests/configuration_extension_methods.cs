using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Tracking;

namespace SqlServerTests;

public class configuration_extension_methods : SqlServerContext
{
    [Fact]
    public void bootstrap_with_connection_string()
    {
        using var host = WolverineHost.For(x =>
            x.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString));
        
        host.GetRuntime().Storage.As<SqlServerMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.SqlServerConnectionString);
    }
}