using System;
using Microsoft.Extensions.Logging;

namespace Wolverine.RabbitMQ.Internal
{
    internal class ConsoleLogger : ILogger
    {
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine(formatter(state, exception));
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return new Disposable();
        }

        internal class Disposable : IDisposable
        {
            public void Dispose()
            {

            }
        }
    }
}
