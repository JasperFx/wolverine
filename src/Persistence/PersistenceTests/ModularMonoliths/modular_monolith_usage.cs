using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.ModularMonoliths;

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
        var stores = (await runtime.Stores.FindAllAsync()).OfType<PostgresqlMessageStore>().ToArray();
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
        var stores = (await runtime.Stores.FindAllAsync()).OfType<PostgresqlMessageStore>().ToArray();
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

                // This declares to Wolverine what the "main" 
                //opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "different");

                opts.Services.AddMartenStore<IPlayerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "players";
                }).IntegrateWithWolverine(x => x.SchemaName = "different");
                
                opts.Services.AddMartenStore<IThingStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "things";
                }).IntegrateWithWolverine(x => x.SchemaName = "different");

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var stores = (await runtime.Stores.FindAllAsync()).OfType<PostgresqlMessageStore>().ToArray();
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

                // Gotta have this for the nodes
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "main");

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

        runtime.Stores.HasAnyAncillaryStores().ShouldBeTrue();
        
        runtime.Stores.FindAncillaryStore(typeof(IPlayerStore)).ShouldBeOfType<PostgresqlMessageStore>() .Settings.SchemaName.ShouldBe("players");
        runtime.Stores.FindAncillaryStore(typeof(IThingStore)).ShouldBeOfType<PostgresqlMessageStore>() .Settings.SchemaName.ShouldBe("things");
    }

}