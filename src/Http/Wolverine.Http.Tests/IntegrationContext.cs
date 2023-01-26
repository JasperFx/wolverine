using Alba;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests;

public class AppFixture : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Workaround for Oakton with WebApplicationBuilder
        // lifecycle issues. Doesn't matter to you w/o Oakton
        OaktonEnvironment.AutoStartHost = true;
         
        // This is bootstrapping the actual application using
        // its implied Program.Main() set up
        Host = await AlbaHost.For<Program>(x =>
        {

        });
    }
 
    public IAlbaHost Host { get; private set; }
 
    public Task DisposeAsync()
    {
        return Host.DisposeAsync().AsTask();
    }
}


[CollectionDefinition("integration")]
public class ScenarioCollection : ICollectionFixture<AppFixture>
{
     
}

[Collection("integration")]
public abstract class IntegrationContext : IAsyncLifetime
{
    public IntegrationContext(AppFixture fixture)
    {
        Host = fixture.Host;
        Store = Host.Services.GetRequiredService<IDocumentStore>();
    }
     
    public IAlbaHost Host { get; }
    public IDocumentStore Store { get; }

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Using Marten, wipe out all data and reset the state
        // back to exactly what we described in InitialAccountData
        await Store.Advanced.ResetAllData();
    }
 
    // This is required because of the IAsyncLifetime 
    // interface. Note that I do *not* tear down database
    // state after the test. That's purposeful
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
    
    // This method allows us to make HTTP calls into our system
    // in memory with Alba, but do so within Wolverine's test support
    // for message tracking to both record outgoing messages and to ensure
    // that any cascaded work spawned by the initial command is completed
    // before passing control back to the calling test
    protected async Task<(ITrackedSession, IScenarioResult)> TrackedHttpCall(Action<Scenario> configuration)
    {
        IScenarioResult result = null;
     
        // The outer part is tying into Wolverine's test support
        // to "wait" for all detected message activity to complete
        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            // The inner part here is actually making an HTTP request
            // to the system under test with Alba
            result = await Host.Scenario(configuration);
        });
 
        return (tracked, result);
    }
}