using Microsoft.Extensions.Hosting;
using Raven.Client.Documents.Session;
using Raven.DependencyInjection;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.RavenDb;

namespace RavenDbTests;

public class DocumentationSamples
{
    public static async Task bootstrap()
    {
        #region sample_bootstrapping_with_ravendb

        var builder = Host.CreateApplicationBuilder();
        
        // You'll need a reference to RavenDB.DependencyInjection
        // for this one
        builder.Services.AddRavenDbDocStore(raven =>
        {
            // configure your RavenDb connection here
        });

        builder.UseWolverine(opts =>
        {
            // That's it, nothing more to see here
            opts.UseRavenDbPersistence();
            
            // The RavenDb integration supports basic transactional
            // middleware just fine
            opts.Policies.AutoApplyTransactions();
        });
        
        // continue with your bootstrapping...

        #endregion
    }
}

#region sample_ravendb_saga

public class Order : Saga
{
    // Just use this for the identity
    // of RavenDb backed sagas
    public string Id { get; set; }
    
    // Handle and Start methods...
}

#endregion

public class CreateDocCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
}


#region sample_using_transactional_with_raven

public class CreateDocCommandHandler
{
    [Transactional]
    public async Task Handle(CreateDocCommand message, IAsyncDocumentSession session)
    {
        await session.StoreAsync(new FakeDoc { Id = message.Id });
    }
}

#endregion

#region sample_raven_using_handler_for_auto_transactions

public class AlternativeCreateDocCommandHandler
{
    // Auto transactions would kick in just because of the dependency
    // on IAsyncDocumentSession
    public async Task Handle(CreateDocCommand message, IAsyncDocumentSession session)
    {
        await session.StoreAsync(new FakeDoc { Id = message.Id });
    }
}

#endregion

public class FakeDoc
{
    public Guid Id { get; set; }
}
