using IntegrationTests;
using Lamar;
using Marten;
using Marten.Metadata;
using Marten.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Marten.MultiTenancy;

public class conjoined_tenancy : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine()
                    .UseLightweightSessions();
                
            }).StartAsync();

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(CreateTenantDocument));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task execute_with_tenancy()
    {
        // In normal usage, only resolve IMessageBus from a scoped
        // container, which either ASP.Net Core or Wolverine will do 
        // for you in normal Controller / Minimal API / Wolverine message
        // handling does for you
        var container = (IContainer)_host.Services;
        using var nested = container.GetNestedContainer();

        var bus = nested.GetInstance<IMessageBus>();

        
        
        bus.TenantId = "one";
        await bus.InvokeAsync(new CreateTenantDocument("Rand Al'Thor", "Andor"));

        throw new Exception("Come back to this");


    }
}

public record CreateTenantDocument(string Name, string Location);

public static class CreateTenantDocumentHandler
{
    public static IMartenOp Handle(CreateTenantDocument command)
    {
        return MartenOps.Insert(new TenantedDocument{Name = command.Name, Location = command.Location});
    }
}

public class TenantedDocument : ITenanted
{
    [Identity]
    public string Name { get; init; }

    public string TenantId { get; set; }
    public string Location { get; set; }
}