using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.Pubsub.Tests.ConventionalRouting;

public abstract class ConventionalRoutingContext : IDisposable {
    private IHost _host = default!;

    internal IWolverineRuntime theRuntime {
        get {
            Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
            Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

            _host ??= WolverineHost.For(opts => opts
                .UsePubsubTesting()
                .AutoProvision()
                .AutoPurgeOnStartup()
                .EnableAllNativeDeadLettering()
                .SystemEndpointsAreEnabled(true)
                .UseConventionalRouting()
            );

            return _host.Services.GetRequiredService<IWolverineRuntime>();
        }
    }

    public void Dispose() {
        _host?.Dispose();
    }

    internal void ConfigureConventions(Action<PubsubMessageRoutingConvention> configure) {
        Environment.SetEnvironmentVariable("PUBSUB_EMULATOR_HOST", "[::1]:8085");
		Environment.SetEnvironmentVariable("PUBSUB_PROJECT_ID", "wolverine");

        _host = Host
            .CreateDefaultBuilder()
            .UseWolverine(opts => {
                opts
                    .UsePubsubTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup()
                    .EnableAllNativeDeadLettering()
                    .SystemEndpointsAreEnabled(true)
                    .UseConventionalRouting(configure);
            }).Start();
    }

    internal IMessageRouter RoutingFor<T>() {
        return theRuntime.RoutingFor(typeof(T));
    }

    internal void AssertNoRoutes<T>() {
        RoutingFor<T>().ShouldBeOfType<EmptyMessageRouter<T>>();
    }

    internal IMessageRoute[] PublishingRoutesFor<T>() {
        return RoutingFor<T>().ShouldBeOfType<MessageRouter<T>>().Routes;
    }
}