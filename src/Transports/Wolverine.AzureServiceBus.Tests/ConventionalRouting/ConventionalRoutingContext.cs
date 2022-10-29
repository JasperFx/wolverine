using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;

namespace Wolverine.AzureServiceBus.Tests.ConventionalRouting
{
    public abstract class ConventionalRoutingContext : IDisposable
    {
        private IHost _host;

        public void Dispose()
        {
            _host?.Dispose();
        }

        internal void ConfigureConventions(Action<AzureServiceBusMessageRoutingConvention> configure)
        {
            _host = Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseAzureServiceBusTesting().UseConventionalRouting(configure).AutoProvision()
                        .AutoPurgeOnStartup();
                }).Start();
        }

        internal IMessageRouter RoutingFor<T>()
        {
            return theRuntime.RoutingFor(typeof(T));
        }

        internal IWolverineRuntime theRuntime
        {
            get
            {
                _host ??= WolverineHost.For(opts => opts.UseAzureServiceBusTesting().UseConventionalRouting().AutoProvision().AutoPurgeOnStartup());

                return _host.Services.GetRequiredService<IWolverineRuntime>();
            }
        }

        internal void AssertNoRoutes<T>()
        {
            RoutingFor<T>().ShouldBeOfType<EmptyMessageRouter<T>>();
        }

        internal MessageRoute[] PublishingRoutesFor<T>()
        {
            return RoutingFor<T>().ShouldBeOfType<MessageRouter<T>>().Routes;
        }


    }
}
