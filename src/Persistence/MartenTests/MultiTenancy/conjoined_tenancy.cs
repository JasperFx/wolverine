using IntegrationTests;
using Marten;
using Marten.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Shouldly;

namespace MartenTests.MultiTenancy;

public class conjoined_tenancy : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        #region sample_setup_with_conjoined_tenancy

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine()
                    .UseLightweightSessions();
                
                opts.Policies.AutoApplyTransactions();
                
            }).StartAsync();

        #endregion

        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(CreateTenantDocument));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    #region sample_using_conjoined_tenancy

    [Fact]
    public async Task execute_with_tenancy()
    {
        var id = Guid.NewGuid();
        
        await _host.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("one", new CreateTenantDocument(id, "Andor")));
        
        await _host.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("two", new CreateTenantDocument(id, "Tear")));
        
        await _host.ExecuteAndWaitAsync(c =>
            c.InvokeForTenantAsync("three", new CreateTenantDocument(id, "Illian")));

        var store = _host.Services.GetRequiredService<IDocumentStore>();

        // Check the first tenant
        using (var session = store.LightweightSession("one"))
        {
            var document = await session.LoadAsync<TenantedDocument>(id);
            document.Location.ShouldBe("Andor");
        }
        
        // Check the second tenant
        using (var session = store.LightweightSession("two"))
        {
            var document = await session.LoadAsync<TenantedDocument>(id);
            document.Location.ShouldBe("Tear");
        }
        
        // Check the third tenant
        using (var session = store.LightweightSession("three"))
        {
            var document = await session.LoadAsync<TenantedDocument>(id);
            document.Location.ShouldBe("Illian");
        }
    }

    #endregion
}

#region sample_conjoined_multi_tenancy_sample_code

// Implementing Marten's ITenanted interface
// also makes Marten treat this document type as
// having "conjoined" multi-tenancy
public class TenantedDocument : ITenanted
{
    public Guid Id { get; init; }

    public string TenantId { get; set; }
    public string Location { get; set; }
}

// A command to create a new document that's multi-tenanted
public record CreateTenantDocument(Guid Id, string Location);

// A message handler to create a new document. Notice there's
// absolutely NO code related to a tenant id, but yet it's
// fully respecting multi-tenancy here in a second
public static class CreateTenantDocumentHandler
{
    public static IMartenOp Handle(CreateTenantDocument command)
    {
        return MartenOps.Insert(new TenantedDocument{Id = command.Id, Location = command.Location});
    }
}

#endregion

