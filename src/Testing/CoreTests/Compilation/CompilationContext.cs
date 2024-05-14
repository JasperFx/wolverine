using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Compilation;

public abstract class CompilationContext : IDisposable
{
    public readonly WolverineOptions theOptions = new();

    private IHost _host;


    protected Envelope theEnvelope;

    public CompilationContext()
    {
        theOptions.DisableConventionalDiscovery();
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    protected void AllHandlersCompileSuccessfully()
    {
        using var host = WolverineHost.For(theOptions);
        host.Get<HandlerGraph>().Chains.Length.ShouldBeGreaterThan(0);
    }

    public MessageHandler HandlerFor<TMessage>()
    {
        if (_host == null)
        {
            _host = WolverineHost.For(theOptions);
        }

        return _host.Get<HandlerGraph>().HandlerFor(typeof(TMessage)).As<MessageHandler>();
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