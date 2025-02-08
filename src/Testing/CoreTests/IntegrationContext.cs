using CoreTests.Acceptance;
using CoreTests.Bugs;
using JasperFx.Core.Reflection;
using Lamar.Microsoft.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using JasperFx.Resources;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests;

public class DefaultApp : IDisposable
{
    public DefaultApp()
    {
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseLamar()
            .UseWolverine(opts =>
            {
                opts.MessageExecutionLogLevel(LogLevel.Information);
                opts.MessageSuccessLogLevel(LogLevel.Debug);
                
                opts.IncludeType<MessageConsumer>();
                opts.IncludeType<InvokedMessageHandler>();

                opts.Services.AddSingleton(Substitute.For<IIdentityService>());
                opts.Services.AddSingleton(Substitute.For<ITrackedTaskRepository>());
            })
            .UseResourceSetupOnStartup(StartupAction.ResetState).Start();
    }

    public IHost Host { get; private set; }

    public void Dispose()
    {
        Host?.Dispose();
        Host = null;
    }

    public void RecycleIfNecessary()
    {
        if (Host == null)
        {
            Host = WolverineHost.Basic();
        }
    }

    public HandlerChain ChainFor<T>()
    {
        return Host.Get<HandlerGraph>().HandlerFor<T>().As<MessageHandler>().Chain;
    }
}

public class IntegrationContext : IDisposable, IClassFixture<DefaultApp>
{
    private readonly DefaultApp _default;

    public IntegrationContext(DefaultApp @default)
    {
        _default = @default;
        _default.RecycleIfNecessary();

        Host = _default.Host;
    }

    public IHost Host { get; private set; }

    public IMessageContext Publisher => new MessageContext(Host.GetRuntime());
    public IMessageBus Bus => Host.MessageBus();

    public HandlerGraph Handlers => Host.Get<HandlerGraph>();

    public virtual void Dispose()
    {
        _default.Dispose();
    }

    protected void with(Action<WolverineOptions> configuration)
    {
        Host = WolverineHost.For(configuration);
    }

    protected HandlerChain chainFor<T>()
    {
        return Handlers.HandlerFor<T>().As<MessageHandler>().Chain;
    }
}