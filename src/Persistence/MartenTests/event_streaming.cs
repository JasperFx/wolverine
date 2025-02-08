using System.Diagnostics;
using IntegrationTests;
using Marten;
using Marten.Events;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit.Abstractions;

namespace MartenTests;

public class event_streaming : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost theReceiver;
    private IHost theSender;

    public event_streaming(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var receiverPort = PortFinder.GetAvailablePort();

        theReceiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtPort(receiverPort);
                opts.Durability.Mode = DurabilityMode.Solo;
            })
            .ConfigureServices(services =>
            {
                services.AddMarten(opts =>
                    {
                        opts.Connection(Servers.PostgresConnectionString);
                        opts.Logger(new TestOutputMartenLogger(_output));
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "receiver");

                services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theReceiver.ResetResourceState();

        theSender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Publish(x =>
                {
                    x.Message<TriggeredEvent>();
                    x.Message<SecondMessage>();

                    x.ToPort(receiverPort).UseDurableOutbox();
                });

                opts.DisableConventionalDiscovery().IncludeType<TriggerHandler>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "sender";
            })
            .ConfigureServices(services =>
            {
                services.AddMarten(opts =>
                    {
                        opts.Connection(Servers.PostgresConnectionString);
                        opts.Logger(new TestOutputMartenLogger(_output));
                    })
                    .IntegrateWithWolverine(x => x.MessageStorageSchemaName = "sender").EventForwardingToWolverine(opts =>
                    {
                        opts.SubscribeToEvent<SecondEvent>().TransformedTo(e => new SecondMessage(e.StreamId, e.Sequence));
                    });

                services.AddResourceSetupOnStartup();
            }).StartAsync();

        await theSender.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        await theReceiver.StopAsync();
        await theSender.StopAsync();

        theReceiver.Dispose();
        theSender.Dispose();
    }

    [Fact]
    public void  preview_routes()
    {
        var routes = theSender.GetRuntime().RoutingFor(typeof(IEvent<ThirdEvent>)).Routes;

        routes.Single().ShouldBeOfType<EventUnwrappingMessageRoute<ThirdEvent>>();
    }

    [Fact]
    public void routing_for_event_type_where_we_handle_IEvent_of_T()
    {
        var runtime = theSender.GetRuntime();

        runtime.RoutingFor(typeof(IEvent<FifthEvent>)).Routes.Single().ShouldBeOfType<MessageRoute>()
            .IsLocal.ShouldBeTrue();

        var routes = runtime.RoutingFor(typeof(FakeEvent<FifthEvent>)).Routes;
        routes.Single().ShouldBeOfType<MessageRoute>().MessageType.ShouldBe(typeof(IEvent<FifthEvent>));
    }

    [Fact]
    public async Task event_should_be_published_from_sender_to_receiver()
    {
        var command = new TriggerCommand();

        var results = await theSender.TrackActivity().AlsoTrack(theReceiver).InvokeMessageAndWaitAsync(command);

        var triggered = results.Received.SingleMessage<TriggeredEvent>();
        triggered.ShouldNotBeNull();
        triggered.Id.ShouldBe(command.Id);

        results.Received.SingleMessage<SecondMessage>()
            .Sequence.ShouldBeGreaterThan(0);

        results.Executed.SingleMessage<ThirdEvent>().ShouldNotBeNull();

        // Can also use IEvent<T> as your handler type!!!
        results.Executed.SingleMessage<IEvent<FifthEvent>>().ShouldNotBeNull();
    }

    #region sample_execution_of_forwarded_events_can_be_awaited_from_tests
    [Fact]
    public async Task execution_of_forwarded_events_can_be_awaited_from_tests()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .ConfigureServices(services =>
            {
                services.AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine().EventForwardingToWolverine(opts =>
                    {
                        opts.SubscribeToEvent<SecondEvent>().TransformedTo(e =>
                            new SecondMessage(e.StreamId, e.Sequence));
                    });
            }).StartAsync();

        var aggregateId = Guid.NewGuid();
        await host.SaveInMartenAndWaitForOutgoingMessagesAsync(session =>
        {
            session.Events.Append(aggregateId, new SecondEvent());
        }, 100_000);

        using var store = host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var events = await session.Events.FetchStreamAsync(aggregateId);
        events.Count.ShouldBe(2);
        events[0].Data.ShouldBeOfType<SecondEvent>();
        events[1].Data.ShouldBeOfType<FourthEvent>();
    }
    #endregion
}

public class TriggerCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

public class TriggerHandler
{
    [Transactional]
    public void Handle(TriggerCommand command, IDocumentSession session)
    {
        session.Events.StartStream(command.Id, new TriggeredEvent { Id = command.Id }, new SecondEvent(), new ThirdEvent(), new FifthEvent());
    }

    public void Handle(ThirdEvent e)
    {
    }

    public void Handle(IEvent<FifthEvent> e)
    {
    }
}

public record SecondMessage(Guid AggregateId, long Sequence);

public class SecondEvent;

public class ThirdEvent;
public class FourthEvent;
public class FifthEvent;

public class TriggeredEvent
{
    public Guid Id { get; set; }
}

public class TriggerEventHandler
{
    private static readonly TaskCompletionSource<TriggeredEvent> _source = new();
    public static Task<TriggeredEvent> Waiter => _source.Task;

    public void Handle(TriggeredEvent message)
    {
        _source.SetResult(message);
    }

    #region sample_execution_of_forwarded_events_second_message_to_fourth_event
    public static Task HandleAsync(SecondMessage message, IDocumentSession session)
    {
        session.Events.Append(message.AggregateId, new FourthEvent());
        return session.SaveChangesAsync();
    }
    #endregion
}

public class TestOutputMartenLogger : IMartenLogger, IMartenSessionLogger, ILogger
{
    private static readonly ITestOutputHelper _noopTestOutputHelper = new NoopTestOutputHelper();
    private readonly ITestOutputHelper _output;

    public TestOutputMartenLogger(ITestOutputHelper output)
    {
        _output = output ?? _noopTestOutputHelper;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (logLevel == LogLevel.Error)
        {
            _output.WriteLine(exception?.ToString());
        }
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        throw new NotImplementedException();
    }

    public IMartenSessionLogger StartSession(IQuerySession session)
    {
        return this;
    }

    public void SchemaChange(string sql)
    {
        _output.WriteLine("Executing DDL change:");
        _output.WriteLine(sql);
        _output.WriteLine(string.Empty);

        Debug.WriteLine("Executing DDL change:");
        Debug.WriteLine(sql);
        Debug.WriteLine(string.Empty);
    }

    public void LogSuccess(NpgsqlCommand command)
    {
        _output.WriteLine(command.CommandText);
        foreach (var p in command.Parameters.OfType<NpgsqlParameter>())
            _output.WriteLine($"  {p.ParameterName}: {p.Value}");

        Debug.WriteLine(command.CommandText);
        foreach (var p in command.Parameters.OfType<NpgsqlParameter>())
            Debug.WriteLine($"  {p.ParameterName}: {p.Value}");
    }

    public void LogFailure(NpgsqlCommand command, Exception ex)
    {
        _output.WriteLine("Postgresql command failed!");
        _output.WriteLine(command.CommandText);
        foreach (var p in command.Parameters.OfType<NpgsqlParameter>())
            _output.WriteLine($"  {p.ParameterName}: {p.Value}");
        _output.WriteLine(ex.ToString());
    }

    public void LogSuccess(NpgsqlBatch batch)
    {
        foreach (var command in batch.BatchCommands)
        {
            _output.WriteLine(command.CommandText);
            foreach (var p in command.Parameters.OfType<NpgsqlParameter>())
                _output.WriteLine($"  {p.ParameterName}: {p.Value}");

            Debug.WriteLine(command.CommandText);
            foreach (var p in command.Parameters.OfType<NpgsqlParameter>())
                Debug.WriteLine($"  {p.ParameterName}: {p.Value}");
        }
    }

    public void LogFailure(NpgsqlBatch batch, Exception ex)
    {
        _output.WriteLine("Postgresql batch failed!");

        foreach (var command in batch.BatchCommands)
        {
            _output.WriteLine(command.CommandText);
            foreach (var p in command.Parameters.OfType<NpgsqlParameter>())
                _output.WriteLine($"  {p.ParameterName}: {p.Value}");
        }

        _output.WriteLine(ex.ToString());
    }

    public void LogFailure(Exception ex, string message)
    {
        _output.WriteLine(message);
        _output.WriteLine(ex.ToString());
    }

    public void RecordSavedChanges(IDocumentSession session, IChangeSet commit)
    {
        var lastCommit = commit;
        _output.WriteLine(
            $"Persisted {lastCommit.Updated.Count()} updates, {lastCommit.Inserted.Count()} inserts, and {lastCommit.Deleted.Count()} deletions");
    }

    public void OnBeforeExecute(NpgsqlCommand command)
    {
    }

    public void OnBeforeExecute(NpgsqlBatch batch)
    {
    }

    private class NoopTestOutputHelper : ITestOutputHelper
    {
        public void WriteLine(string message)
        {
        }

        public void WriteLine(string format, params object[] args)
        {
        }
    }
}