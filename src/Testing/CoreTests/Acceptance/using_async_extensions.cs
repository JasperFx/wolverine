using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;
using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Transports.Local;
using Xunit;

namespace CoreTests.Acceptance;

public class using_async_extensions
{

    [Fact]
    public async Task apply_async_extension_with_feature_flag_positive()
    {
        var featureManager = Substitute.For<IFeatureManager>();
        featureManager.IsEnabledAsync("Module1").Returns(true);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddFeatureManagement();
                opts.Services.AddSingleton(featureManager);

                opts.Services.AddAsyncWolverineExtension<SampleAsyncExtension>();

            }).StartAsync();

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        var queue = runtime.Options.Transports.TryGetEndpoint(new Uri("local://module1-high-priority"));

        queue.ShouldNotBeNull();
        queue.ExecutionOptions.EnsureOrdered.ShouldBeTrue();
    }

    [Fact]
    public async Task apply_async_extension_with_feature_flag_negative()
    {
        var featureManager = Substitute.For<IFeatureManager>();
        featureManager.IsEnabledAsync("Module1").Returns(false);

        #region sample_registering_async_extension

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddFeatureManagement();
                opts.Services.AddSingleton(featureManager);

                // Adding the async extension to the underlying IoC container
                opts.Services.AddAsyncWolverineExtension<SampleAsyncExtension>();

            }).StartAsync();

        #endregion

        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.Options.Transports.GetOrCreate<LocalTransport>().AllQueues()
            .Any(x => x.EndpointName == "module1-high-priority").ShouldBeFalse();

    }
}

#region sample_async_Wolverine_extension

public class SampleAsyncExtension : IAsyncWolverineExtension
{
    private readonly IFeatureManager _features;

    public SampleAsyncExtension(IFeatureManager features)
    {
        _features = features;
    }

    public async ValueTask Configure(WolverineOptions options)
    {
        if (await _features.IsEnabledAsync("Module1"))
        {
            // Make any kind of Wolverine configuration
            options
                .PublishMessage<Module1Message>()
                .ToLocalQueue("module1-high-priority")
                .Sequential();
        }
    }
}

#endregion

public class Module1Message;