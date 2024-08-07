using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Compilation;

public abstract class CompilationContext : IDisposable
{
    private IHost _host;


    protected Envelope theEnvelope;

    public void Dispose()
    {
        _host?.Dispose();
    }

    protected void IfWolverineIsConfiguredAs(Action<WolverineOptions> configure)
    {
        _host = Host.CreateDefaultBuilder().UseWolverine(configure).Start();
    }

    protected void AllHandlersCompileSuccessfully(Action<WolverineOptions>? configure = null)
    {
        configure ??= _ => { };
        using var host = WolverineHost.For(configure);
        host.Get<HandlerGraph>().Chains.Length.ShouldBeGreaterThan(0);
    }

    public MessageHandler HandlerFor<TMessage>(Action<WolverineOptions>? configure = null)
    {
        if (_host == null)
        {
            configure ??= _ => { };
            _host = WolverineHost.For(configure);
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