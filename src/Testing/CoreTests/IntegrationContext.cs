using System;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using TestingSupport.Compliance;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests;

public class DefaultApp : IDisposable
{
    public DefaultApp()
    {
        Host = WolverineHost.For(x =>
        {
            x.IncludeType<MessageConsumer>();
            x.IncludeType<InvokedMessageHandler>();
        });
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
        return Host.Get<HandlerGraph>().ChainFor<T>();
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

    public IMessageContext Publisher => Host.Get<IMessageContext>();
    public IMessageBus Bus => Host.Get<IMessageBus>();

    public HandlerGraph Handlers => Host.Get<HandlerGraph>();

    public virtual void Dispose()
    {
        _default.Dispose();
    }

    protected void with(Action<WolverineOptions> configuration)
    {
        Host = WolverineHost.For(opts =>
        {
            configuration(opts);
            opts.Services.Scan(scan =>
            {
                scan.TheCallingAssembly();
                scan.WithDefaultConventions();
            });
        });
    }

    protected HandlerChain chainFor<T>()
    {
        return Handlers.ChainFor<T>();
    }
}