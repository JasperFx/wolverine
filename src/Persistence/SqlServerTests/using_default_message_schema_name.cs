using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Persistence;

namespace SqlServerTests;

public class using_default_message_schema_name
{
    [Fact]
    public async Task use_default_schema_name_when_specified_for_connection_string()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine_default";
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IMessageStore>();
        store.ShouldBeOfType<SqlServerMessageStore>().Settings.SchemaName.ShouldBe("wolverine_default");
    }

    [Fact]
    public async Task override_the_storage_schema()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine_default";
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "non_default");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IMessageStore>();
        store.ShouldBeOfType<SqlServerMessageStore>().Settings.SchemaName.ShouldBe("non_default");
    }
}