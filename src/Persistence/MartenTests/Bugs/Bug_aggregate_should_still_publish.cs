using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using MartenTests.AggregateHandlerWorkflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

// Originally reported by JurJean in https://github.com/JasperFx/wolverine/pull/2922: a handler that
// loads an aggregate with [ReadAggregate] appeared to "lose" its cascading message. The root cause
// is NOT a bug in the outbox - it is the *AggregateHandler naming convention. A handler whose type
// name ends with "AggregateHandler" is automatically promoted into Marten's aggregate event-sourcing
// workflow (MartenAggregateHandlerStrategy), where the return value is appended to the aggregate's
// event stream rather than published as a cascading message. Rename the handler and the return value
// is published normally.
//
// These tests pin that contract deterministically (the original repro counted the inbox table
// immediately after PublishAsync, before the async processing had run, which is racy).
public class Bug_aggregate_should_still_publish : PostgresqlContext, IClassFixture<AggregatePublishContext>
{
    private readonly AggregatePublishContext _context;

    public Bug_aggregate_should_still_publish(AggregatePublishContext context)
    {
        _context = context;
    }

    private IHost theHost => _context.Host;

    [Fact]
    public async Task normal_handler_publishes_its_cascading_message()
    {
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<SomethingWasScheduled>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new ScheduleSomething(Guid.NewGuid())));

        tracked.Received.MessagesOf<SomethingWasScheduled>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task read_aggregate_handler_with_safe_name_publishes_its_cascading_message()
    {
        // ScheduleReader uses [ReadAggregate] exactly like the original repro, but its type name does
        // NOT end with "AggregateHandler", so the return value is published as a cascading message.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<SomethingWasScheduled>(theHost)
            .ExecuteAndWaitAsync(_ => theHost.MessageBus().PublishAsync(new ScheduleViaReader(Guid.NewGuid())));

        tracked.Received.MessagesOf<SomethingWasScheduled>().Count().ShouldBe(1);
    }

    [Fact]
    public async Task aggregate_named_handler_captures_the_return_value_as_an_event_not_a_message()
    {
        // The original repro's handler was named "AggregateHandler", which opts it into the aggregate
        // event-sourcing workflow: the return value is appended to the event stream, NOT published. So
        // no SomethingWasScheduled message is ever produced.
        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(_ =>
                theHost.MessageBus().PublishAsync(new ScheduleSomethingUsingAggregate(Guid.NewGuid())));

        tracked.Received.MessagesOf<ScheduleSomethingUsingAggregate>().Count().ShouldBe(1);
        tracked.Received.MessagesOf<SomethingWasScheduled>().ShouldBeEmpty();
    }

    [Fact]
    public void warns_when_a_readaggregate_handler_is_promoted_by_the_aggregatehandler_naming_convention()
    {
        // GH-2922 guardrail: bootstrapping should have logged a warning for AggregateHandler, which uses
        // [ReadAggregate] and is auto-promoted into the aggregate workflow purely by its name.
        _context.Warnings.ShouldContain(w =>
            w.Contains(typeof(AggregateHandler).FullName!) && w.Contains("AggregateHandler"));
    }
}

public class AggregatePublishContext : PostgresqlContext, IAsyncLifetime
{
    private const string Schema = "publish_2922";

    public IHost Host { get; private set; } = null!;

    public ConcurrentBag<string> Warnings { get; } = new();

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync(Schema);
            await conn.CloseAsync();
        }

        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(AggregateHandler))
                    .IncludeType(typeof(ScheduleReader))
                    .IncludeType(typeof(SomeOtherHandler));

                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);

                        m.DatabaseSchemaName = Schema;
                        m.DisableNpgsqlLogging = true;
                    })
                    .UseLightweightSessions()
                    .IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup();

                opts.Services.AddLogging(b => b.AddProvider(new CapturingLoggerProvider(Warnings)));
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

internal sealed class CapturingLoggerProvider(ConcurrentBag<string> warnings) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(warnings);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ConcurrentBag<string> warnings) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}

public record ScheduleSomething(Guid Id);

public record ScheduleSomethingUsingAggregate(Guid Id);

public record ScheduleViaReader(Guid Id);

public record SomethingWasScheduled(Guid Id);

// Name ends with "AggregateHandler" -> auto-promoted into the Marten aggregate event-sourcing
// workflow, so the return value is appended to the LetterAggregate stream as an event.
public static class AggregateHandler
{
    public static SomethingWasScheduled Handle(
        ScheduleSomethingUsingAggregate command,
        [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return new SomethingWasScheduled(command.Id);
    }
}

// Same [ReadAggregate] usage, but the type name does not end with "AggregateHandler", so the return
// value is published as a cascading message.
public static class ScheduleReader
{
    public static SomethingWasScheduled Handle(
        ScheduleViaReader command,
        [ReadAggregate(Required = false)] LetterAggregate aggregate)
    {
        return new SomethingWasScheduled(command.Id);
    }
}

public static class SomeOtherHandler
{
    public static SomethingWasScheduled Handle(ScheduleSomething command)
    {
        return new SomethingWasScheduled(command.Id);
    }

    public static void Handle(SomethingWasScheduled message)
    {
    }
}
