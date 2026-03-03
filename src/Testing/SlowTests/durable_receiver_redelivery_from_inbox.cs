using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;
using Wolverine.Transports.Local;
using Xunit;

namespace SlowTests;

public class durable_receiver_redelivery_from_inbox : IAsyncLifetime
{
    private readonly string _schemaName = $"durable_receiver_{Guid.NewGuid():N}";
    private IHost? _host;
    private IWolverineRuntime _runtime = null!;
    private readonly List<Exception> _exceptions = new();

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new ListLoggerProvider(_exceptions));
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, _schemaName);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        await _host.ResetResourceState();
        _runtime = _host.Services.GetRequiredService<IWolverineRuntime>();
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task redelivery_from_inbox_can_hit_latched_receiver_without_listener()
    {
        var pipeline = Substitute.For<IHandlerPipeline>();
        var endpoint = new LocalQueue("one")
        {
            Mode = EndpointMode.Durable
        };
        var receiver = new DurableReceiver(endpoint, _runtime, pipeline);
        var listener = Substitute.For<IListener>();

        var envelope = ObjectMother.Envelope();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Destination = endpoint.Uri;

        await _runtime.Storage.Inbox.StoreIncomingAsync(envelope);

        var loaded = (await _runtime.Storage.Admin.AllIncomingAsync())
            .Single(x => x.Id == envelope.Id);

        receiver.Latch();

        await receiver.ReceivedAsync(listener, loaded);

        await Task.Delay(250.Milliseconds());

        _exceptions.Any(containsNullReferenceException).ShouldBeTrue();

        loaded.Listener.ShouldBeNull();
    }

    private static bool containsNullReferenceException(Exception exception)
    {
        if (exception is NullReferenceException)
        {
            return true;
        }

        var inner = exception.InnerException;
        while (inner != null)
        {
            if (inner is NullReferenceException)
            {
                return true;
            }

            inner = inner.InnerException;
        }

        return false;
    }
}

internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly List<Exception> _exceptions;

    public ListLoggerProvider(List<Exception> exceptions)
    {
        _exceptions = exceptions;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new ListLogger(_exceptions);
    }

    public void Dispose()
    {
    }
}

internal sealed class ListLogger : ILogger
{
    private readonly List<Exception> _exceptions;

    public ListLogger(List<Exception> exceptions)
    {
        _exceptions = exceptions;
    }

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception != null)
        {
            _exceptions.Add(exception);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}
