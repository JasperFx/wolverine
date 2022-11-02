using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.RabbitMQ.Internal
{
    internal class ParallelRabbitMqListener : IListener, IDisposable
    {
        private readonly IList<RabbitMqListener> _listeners = new List<RabbitMqListener>();
        private readonly RabbitMqChannelCallback _callback;

        public ParallelRabbitMqListener(IWolverineRuntime logger,
            RabbitMqQueue endpoint, RabbitMqTransport transport, IReceiver receiver)
        {
            Address = endpoint.Uri;
            for (var i = 0; i < endpoint.ListenerCount; i++)
            {
                var listener = new RabbitMqListener(logger, endpoint, transport, receiver);
                _listeners.Add(listener);
            }

            _callback = transport.Callback;
        }

        public void Dispose()
        {
            foreach (var listener in _listeners) listener.SafeDispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public Uri Address { get; }
        public ValueTask StopAsync()
        {
            foreach (var listener in _listeners)
            {
                listener.Stop();
            }

            return ValueTask.CompletedTask;
        }

        public Task<bool> TryRequeueAsync(Envelope envelope)
        {
            var listener = _listeners.FirstOrDefault();
            return listener != null ? listener.TryRequeueAsync(envelope) : Task.FromResult(false);
        }

        public ValueTask CompleteAsync(Envelope envelope)
        {
            return _callback.CompleteAsync(envelope);
        }

        public ValueTask DeferAsync(Envelope envelope)
        {
            return _callback.DeferAsync(envelope);
        }
    }
}
