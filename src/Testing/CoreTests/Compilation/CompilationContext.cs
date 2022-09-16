using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Compilation;

public abstract class CompilationContext : IDisposable
{
    public readonly WolverineOptions theOptions = new WolverineOptions();

    private IHost _host;


    protected Envelope theEnvelope;

    public CompilationContext()
    {
        theOptions.Handlers.DisableConventionalDiscovery();
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    protected void AllHandlersCompileSuccessfully()
    {
        using (var runtime = WolverineHost.For(theOptions))
        {
            runtime.Get<HandlerGraph>().Chains.Length.ShouldBeGreaterThan(0);
        }
    }

    public MessageHandler HandlerFor<TMessage>()
    {
        if (_host == null)
        {
            _host = WolverineHost.For(theOptions);
        }


        return _host.Get<HandlerGraph>().HandlerFor(typeof(TMessage));
    }

    public async Task<IMessageContext> Execute<TMessage>(TMessage message)
    {
        var handler = HandlerFor<TMessage>();
        theEnvelope = new Envelope(message);
        var context = new MessageContext(_host.Get<IWolverineRuntime>());
        context.ReadEnvelope(theEnvelope, InvocationCallback.Instance);

        await handler.HandleAsync(context, default);

        return context;
    }

    [Fact]
    public void can_compile_all()
    {
        AllHandlersCompileSuccessfully();
    }
}
