using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Compilation;

public abstract class CompilationContext : IDisposable
{
    private IHost _host = null!;


    protected Envelope theEnvelope = null!;

    public void Dispose()
    {
        _host?.Dispose();
    }

    protected void IfWolverineIsConfiguredAs(Action<WolverineOptions> configure)
    {
        _host = Host.CreateDefaultBuilder().UseWolverine(configure).Start();
    }

    protected async Task AllHandlersCompileSuccessfully(Action<WolverineOptions>? configure = null)
    {
        configure ??= _ => { };
        using var host = await WolverineHost.ForAsync(configure);
        host.Get<HandlerGraph>().Chains.Length.ShouldBeGreaterThan(0);
    }

    public async Task<MessageHandler> HandlerFor<TMessage>(Action<WolverineOptions>? configure = null)
    {
        if (_host == null)
        {
            configure ??= _ => { };
            _host = await WolverineHost.ForAsync(configure);
        }

        return _host.Get<HandlerGraph>().HandlerFor(typeof(TMessage))!.As<MessageHandler>();
    }

    public Task<IMessageContext> Execute<TMessage>(TMessage message) => Execute(message, null);

    /// <summary>
    /// GH-5136. Run the chain of <typeparamref name="TMessage"/> that belongs to <paramref name="handlerType"/>.
    /// Each handler type has its own chain, and the shared messages are handled by several handler types here
    /// </summary>
    public async Task<IMessageContext> Execute<TMessage>(TMessage message, Type? handlerType)
    {
        var handler = handlerType == null
            ? await HandlerFor<TMessage>()
            : _host.Get<HandlerGraph>().HandlerForHandlerType(typeof(TMessage), handlerType)!.As<MessageHandler>();
        theEnvelope = new Envelope(message!);
        var context = new MessageContext(_host.Get<IWolverineRuntime>());
        context.ReadEnvelope(theEnvelope, InvocationCallback.Instance);

        await handler.HandleAsync(context, default);

        return context;
    }

    [Fact]
    public async Task can_compile_all()
    {
        await AllHandlersCompileSuccessfully();
    }
}