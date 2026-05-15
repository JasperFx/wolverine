using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace Wolverine.ComplianceTests.Sagas;


public class SagaTestHarness<T> : IDisposable
    where T : Saga
{
    private IHost _host = null!;

    public SagaTestHarness(ISagaHost sagaHost)
    {
        SagaHost = sagaHost;
    }

    public ISagaHost SagaHost { get; }

    public void Dispose()
    {
        _host?.Dispose();
    }

    protected async Task withApplication()
    {
        _host = await SagaHost.BuildHostAsync<T>();
    }

    protected async Task<string> codeFor<TMessage>()
    {
        if (_host == null)
        {
            await withApplication();
        }

        return _host!.Get<HandlerGraph>().HandlerFor<TMessage>()!.As<MessageHandler>().Chain!.SourceCode!;
    }

    protected async Task invoke<TMessage>(TMessage message)
    {
        if (_host == null)
        {
            await withApplication();
        }

        await _host!.InvokeMessageAndWaitAsync(message!);
    }

    protected async Task send<TMessage>(TMessage message)
    {
        if (_host == null)
        {
            await withApplication();
        }

        await _host!.ExecuteAndWaitValueTaskAsync(x => x.SendAsync(message!));
    }

    protected Task send<TMessage>(TMessage message, object sagaId)
    {
        return _host.SendMessageAndWaitAsync(message, new DeliveryOptions { SagaId = sagaId.ToString() }, 10000);
    }

    protected Task<T?> LoadState(Guid id)
    {
        return SagaHost.LoadState<T>(id);
    }

    protected Task<T?> LoadState(string id)
    {
        return SagaHost.LoadState<T>(id);
    }

    protected Task<T?> LoadState(int id)
    {
        return SagaHost.LoadState<T>(id);
    }

    protected Task<T?> LoadState(long id)
    {
        return SagaHost.LoadState<T>(id);
    }
}