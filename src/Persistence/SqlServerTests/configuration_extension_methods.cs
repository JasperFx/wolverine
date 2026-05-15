using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Weasel.Core.Migrations;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;
using Wolverine.Tracking;

namespace SqlServerTests;

public class configuration_extension_methods : SqlServerContext
{
    [Fact]
    public async Task bootstrap_with_connection_string()
    {
        using var host = await WolverineHost.ForAsync(x =>
            x.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString));

        host.GetRuntime().Storage.As<SqlServerMessageStore>()
            .Settings.ConnectionString.ShouldBe(Servers.SqlServerConnectionString);
    }
}