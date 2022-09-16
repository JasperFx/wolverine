using System;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using TestingSupport.Compliance;
using TestMessages;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests;

public class DefaultApp : IDisposable
{
    public DefaultApp()
    {
        Host = WolverineHost.For(x =>
        {
            x.Handlers.IncludeType<MessageConsumer>();
            x.Handlers.IncludeType<InvokedMessageHandler>();
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
    public ICommandBus Bus => Host.Get<ICommandBus>();

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
