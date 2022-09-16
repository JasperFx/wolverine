using System;
using System.Threading.Tasks;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Microsoft.Extensions.Hosting;

namespace TestingSupport.Sagas
{
    public class SagaTestHarness<T> : IDisposable
        where T : Saga
    {
        public ISagaHost SagaHost { get; }
        private IHost _host;

        public SagaTestHarness(ISagaHost sagaHost)
        {
            SagaHost = sagaHost;
        }

        public void Dispose()
        {
            _host?.Dispose();
        }



        protected void withApplication()
        {
            _host = SagaHost.BuildHost<T>();

        }

        protected string codeFor<T>()
        {
            return _host.Get<HandlerGraph>().HandlerFor<T>().Chain.SourceCode;
        }

        protected async Task invoke<T>(T message)
        {
            if (_host == null) withApplication();

            await _host.Get<IMessagePublisher>().InvokeAsync(message);
        }

        protected async Task send<T>(T message)
        {
            if (_host == null) withApplication();

            await _host.ExecuteAndWaitValueTaskAsync(x => x.SendAsync(message));
        }

        protected Task send<T>(T message, object sagaId)
        {
            return _host.SendMessageAndWaitAsync(message, new DeliveryOptions { SagaId = sagaId.ToString() }, 10000);
        }

        protected Task<T> LoadState(Guid id)
        {
            return SagaHost.LoadState<T>(id);
        }

        protected Task<T> LoadState(string id)
        {
            return SagaHost.LoadState<T>(id);
        }

        protected Task<T> LoadState(int id)
        {
            return SagaHost.LoadState<T>(id);
        }

        protected Task<T> LoadState(long id)
        {
            return SagaHost.LoadState<T>(id);
        }
    }

}
