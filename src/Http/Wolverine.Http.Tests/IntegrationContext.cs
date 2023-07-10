using Alba;
using Marten;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Oakton;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests;

public class AppFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; }

    public async Task InitializeAsync()
    {
        // Sorry folks, but this is absolutely necessary if you 
        // use Oakton for command line processing and want to 
        // use WebApplicationFactory and/or Alba for integration testing
        OaktonEnvironment.AutoStartHost = true;

        // This is bootstrapping the actual application using
        // its implied Program.Main() set up
        Host = await AlbaHost.For<Program>(x => { });
    }

    public Task DisposeAsync()
    {
        if (Host != null)
        {
            return Host.DisposeAsync().AsTask();
        }

        return Task.CompletedTask;
    }

    private async Task bootstrap(int delay)
    {
        if (Host != null)
        {
            try
            {
                var endpoints = Host.Services.GetRequiredService<EndpointDataSource>().Endpoints;
                if (endpoints.Count < 5)
                {
                    throw new Exception($"Only got {endpoints.Count} endpoints, something is missing!");
                }

                await Host.GetAsText("/trace");
                await Task.Delay(delay);
                return;
            }
            catch (Exception e)
            {
                await Host.StopAsync();
                Host = null;
            }
        }


    }

    public async Task ResetHost()
    {
        var delay = 0;
        while (true)
        {
            if (delay > 1000)
            {
                throw new Exception("Will not start up, don't know why!");
            }

            try
            {
                await bootstrap(delay);
                break;
            }
            catch (Exception e)
            {
                delay += 100;
                await Task.Delay(delay);

                if (Host != null)
                {
                    await Host.GetAsText("/trace");
                }

                break;
            }
        }
    }
}

[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<AppFixture>
{
}

[Collection("integration")]
public abstract class IntegrationContext : IAsyncLifetime
{
    private readonly AppFixture _fixture;

    protected IntegrationContext(AppFixture fixture)
    {
        _fixture = fixture;
    }
    
    // more....

    public HttpGraph HttpChains => Host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;

    public IAlbaHost Host => _fixture.Host;
    public IDocumentStore Store => _fixture.Host.Services.GetRequiredService<IDocumentStore>();

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


    public async Task<IScenarioResult> Scenario(Action<Scenario> configure)
    {
        try
        {
            return await Host.Scenario(configure);
        }
        catch (Exception e)
        {
            if (e.Message.Contains("but was 404"))
            {
                await _fixture.ResetHost();
                return await Host.Scenario(configure);
            }

            throw;
        }
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