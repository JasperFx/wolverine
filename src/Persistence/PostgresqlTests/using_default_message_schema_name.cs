using IntegrationTests;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;

namespace PostgresqlTests;

public class using_default_message_schema_name
{
    [Fact]
    public async Task use_default_schema_name_when_specified_for_connection_string()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine_default";
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IMessageStore>();
        store.ShouldBeOfType<PostgresqlMessageStore>().Settings.SchemaName.ShouldBe("wolverine_default");
    }

    [Fact]
    public async Task override_the_storage_schema()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine_default";
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "non_default");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IMessageStore>();
        store.ShouldBeOfType<PostgresqlMessageStore>().Settings.SchemaName.ShouldBe("non_default");
    }
    
    [Fact]
    public async Task use_default_schema_name_when_specified_for_data_source()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine_default";
                opts.PersistMessagesWithPostgresql(NpgsqlDataSource.Create(Servers.PostgresConnectionString));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var store = host.Services.GetRequiredService<IMessageStore>();
        store.ShouldBeOfType<PostgresqlMessageStore>().Settings.SchemaName.ShouldBe("wolverine_default");
    }
}