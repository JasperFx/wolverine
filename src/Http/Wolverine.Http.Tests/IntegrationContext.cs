using Alba;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using JasperFx.CommandLine;
using Shouldly;
using Swashbuckle.AspNetCore.Swagger;
using Wolverine.Tracking;
using WolverineWebApi.TestSupport;
using JasperFx;

namespace Wolverine.Http.Tests;

public class AppFixture : IAsyncLifetime
{
    public IAlbaHost? Host { get; private set; }

    public async Task InitializeAsync()
    {
        // Sorry folks, but this is absolutely necessary if you
        // use JasperFx for command line processing and want to
        // use WebApplicationFactory and/or Alba for integration testing
        JasperFxEnvironment.AutoStartHost = true;

        // For "integration" test collection (based on this fixture) ApplicationAssembly is WolverineWebApi.
        // If not set explicitly here other tests may set it to the test assembly causing issues with endpoints discovery.
        JasperFxOptions.RememberedApplicationAssembly = typeof(WolverineWebApi.Program).Assembly;

        #region sample_using_run_wolverine_in_solo_mode_with_extension

        // This is bootstrapping the actual application using
        // its implied Program.Main() set up
        // For non-Alba users, this is using IWebHostBuilder 
        Host = await AlbaHost.For<WolverineWebApi.Program>(x =>
        {
            x.ConfigureServices(services =>
            {
                // Override the Wolverine configuration in the application
                // to run the application in "solo" mode for faster
                // testing cold starts
                services.RunWolverineInSoloMode();

                // And just for completion, disable all Wolverine external 
                // messaging transports
                services.DisableAllExternalWolverineTransports();
            });
        });

        #endregion
    }

    public async Task DisposeAsync()
    {
        if (Host is null)
            return;

        await Host.GetRuntime().StopAsync(default);
        await Host.StopAsync();

        await Host.DisposeAsync();
        Host = null;
    }
}

[CollectionDefinition("integration")]
public class IntegrationCollection : ICollectionFixture<AppFixture>;

[Collection("integration")]
public abstract class IntegrationContext : IAsyncLifetime, IOpenApiSource
{
    private readonly AppFixture _fixture;

    protected IntegrationContext(AppFixture fixture)
    {
        _fixture = fixture;
    }

    // more....

    public HttpGraph HttpChains => Host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;

    public IAlbaHost Host => _fixture.Host!;

    public IDocumentStore Store => Host.Services.GetRequiredService<IDocumentStore>();

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
        return await Host.Scenario(configure);
    }

    // This method allows us to make HTTP calls into our system
    // in memory with Alba, but do so within Wolverine's test support
    // for message tracking to both record outgoing messages and to ensure
    // that any cascaded work spawned by the initial command is completed
    // before passing control back to the calling test
    protected async Task<(ITrackedSession, IScenarioResult)> TrackedHttpCall(Action<Scenario> configuration, int timeoutInMilliseconds = 5000)
    {
        IScenarioResult result = null!;

        // The outer part is tying into Wolverine's test support
        // to "wait" for all detected message activity to complete
        var tracked = await Host.ExecuteAndWaitAsync(async () =>
        {
            // The inner part here is actually making an HTTP request
            // to the system under test with Alba
            result = await Host.Scenario(configuration);
        }, timeoutInMilliseconds);

        return (tracked, result);
    }

    protected Endpoint EndpointFor(string routePattern)
    {
        var endpoint = Host.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>().FirstOrDefault(x => x.RoutePattern.RawText == routePattern);

        endpoint.ShouldNotBeNull();

        return endpoint;
    }

    protected (OpenApiPathItem, OpenApiOperation) FindOpenApiDocument(string path)
    {
        var swagger = Host.Services.GetRequiredService<ISwaggerProvider>();
        var document = swagger.GetSwagger("v1");

        if (document.Paths.TryGetValue(path, out var item))
        {
            return (item, item.Operations.Values.Single());
        }

        throw new Exception($"Unable to find {path}");
    }

    public (OpenApiPathItem, OpenApiOperation) FindOpenApiDocument(OperationType httpMethod, string path)
    {
        var swagger = Host.Services.GetRequiredService<ISwaggerProvider>();
        var document = swagger.GetSwagger("v1");

        if (document.Paths.TryGetValue(path, out var item))
        {
            if (item.Operations.TryGetValue(httpMethod, out var operation))
            {
                return (item, operation);
            }
        }

        throw new Exception($"Unable to find {httpMethod} {path}");
    }
}