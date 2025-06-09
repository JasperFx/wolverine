using IntegrationTests;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests;

public interface IPlayerStore : IDocumentStore;
public interface IThingStore : IDocumentStore;


public class modular_monolith_usage
{
    [Fact]
    public async Task set_the_default_message_store_schema_name()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";

                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine();
                
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var stores = runtime.AncillaryStores.OfType<PostgresqlMessageStore>();
        stores.Any().ShouldBeTrue();

        foreach (var store in stores)
        {
            store.Settings.SchemaName.ShouldBe("wolverine");
        }
    }
    
    [Fact]
    public async Task set_the_default_message_store_schema_name_2()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine();
                
                // Proving out that ordering doesn't matter
                opts.Durability.MessageStorageSchemaName = "wolverine";
                
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var stores = runtime.AncillaryStores.OfType<PostgresqlMessageStore>();
        stores.Any().ShouldBeTrue();

        foreach (var store in stores)
        {
            store.Settings.SchemaName.ShouldBe("wolverine");
        }
    }
    
    [Fact]
    public async Task do_not_override_when_the_schema_name_is_explicitly_set()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";

                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine(schemaName:"different");
                
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine(schemaName:"different");
            }).StartAsync();

        var runtime = host.GetRuntime();
        var stores = runtime.AncillaryStores.OfType<PostgresqlMessageStore>();
        stores.Any().ShouldBeTrue();

        foreach (var store in stores)
        {
            store.Settings.SchemaName.ShouldBe("different");
        }
    }
    
    [Fact]
    public async Task using_the_marten_schema_name_with_no_other_settings()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                //opts.Durability.MessageStorageSchemaName = "wolverine";

                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine();
                
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var stores = runtime.AncillaryStores.OfType<PostgresqlMessageStore>();

        stores.OfType<IAncillaryMessageStore<IPlayerStore>>().Single().As<PostgresqlMessageStore>()
            .Settings.SchemaName.ShouldBe("players");
        
        stores.OfType<IAncillaryMessageStore<IThingStore>>().Single().As<PostgresqlMessageStore>()
            .Settings.SchemaName.ShouldBe("things");
    }
}