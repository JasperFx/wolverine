using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-5224. IWolverineObserver.MessageCausedBy used to report the generated MessageHandler subclass
/// (Internal.Generated.WolverineHandlers.CausationNamingCommandHandler123456) as the handler type,
/// while the message.handler span tag for the very same handler named the user's handler type.
/// Both now come from one codegen-time value.
/// </summary>
public class message_causation_handler_type_5224 : IAsyncLifetime
{
    private readonly List<Activity> _capturedActivities = new();
    private ActivityListener _listener = null!;

    public ValueTask InitializeAsync()
    {
        // The causation latch is static for the life of the process; start from a known state so an edge
        // reported by an earlier test cannot suppress the report this test asserts on.
        CausationLatch.Clear();

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_capturedActivities) _capturedActivities.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(_listener);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Task<IHost> startHostAsync(params Type[] handlerTypes)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Tracking.EnableMessageCausationTracking = true;

                var discovery = opts.Discovery.DisableConventionalDiscovery();
                discovery.IncludeType<CausationNamingEventHandler>();
                foreach (var handlerType in handlerTypes)
                {
                    discovery.IncludeType(handlerType);
                }
            })
            .StartAsync();
    }

    private string messageHandlerTagFor(string handlerName)
    {
        lock (_capturedActivities)
        {
            return _capturedActivities
                .Select(a => a.GetTagItem(WolverineTracing.MessageHandler) as string)
                .OfType<string>()
                .First(x => x.Contains(handlerName));
        }
    }

    [Fact]
    public async Task message_caused_by_reports_the_user_handler_type_not_the_generated_class()
    {
        using var host = await startHostAsync(typeof(CausationNamingCommandHandler));

        var observer = new CausationCapturingObserver();
        host.GetRuntime().Observer = observer;

        await host.MessageBus().InvokeAsync(new CausationNamingCommand("hello"), TestContext.Current.CancellationToken);

        var caused = observer.Caused.ShouldHaveSingleItem();
        caused.Incoming.ShouldBe(typeof(CausationNamingCommand).FullName);
        caused.Outgoing.ShouldBe(typeof(CausationNamingEvent).FullName);

        caused.Handler.ShouldBe(typeof(CausationNamingCommandHandler).FullNameInCode());
        caused.Handler.ShouldNotContain("Internal.Generated");

        // The two observation paths agree on the handler's name
        caused.Handler.ShouldBe(messageHandlerTagFor(nameof(CausationNamingCommandHandler)));
    }

    [Fact]
    public async Task generated_code_passes_the_handler_type_name_to_record_cause_and_effect()
    {
        using var host = await startHostAsync(typeof(CausationNamingCommandHandler));

        var runtime = host.GetRuntime();
        runtime.Handlers.HandlerFor<CausationNamingCommand>();
        var source = runtime.Handlers.ChainFor<CausationNamingCommand>()!.SourceCode;

        source.ShouldNotBeNull();
        source.ShouldContain(
            $"{nameof(MessageHandler.RecordCauseAndEffect)}(context, context.Runtime.Observer, \"{typeof(CausationNamingCommandHandler).FullNameInCode()}\");");
    }

    [Fact]
    public async Task a_chain_with_several_handler_types_reports_the_same_joined_name_as_the_span_tag()
    {
        // The default ClassicCombineIntoOneLogicalHandler mode puts both handler types in one chain.
        // Causation cannot tell which handler call produced which outgoing message, so the chain reports
        // the same joined name the message.handler tag carries.
        using var host = await startHostAsync(typeof(CausationNamingCommandHandler),
            typeof(SecondCausationNamingCommandHandler));

        var observer = new CausationCapturingObserver();
        host.GetRuntime().Observer = observer;

        await host.MessageBus().InvokeAsync(new CausationNamingCommand("hello"), TestContext.Current.CancellationToken);

        var caused = observer.Caused.ShouldHaveSingleItem();
        caused.Handler.ShouldContain(typeof(CausationNamingCommandHandler).FullNameInCode());
        caused.Handler.ShouldContain(typeof(SecondCausationNamingCommandHandler).FullNameInCode());
        caused.Handler.ShouldBe(messageHandlerTagFor(nameof(SecondCausationNamingCommandHandler)));
    }

    [Fact]
    public async Task the_two_argument_overload_still_reports_the_generated_class_for_previously_generated_code()
    {
        // Handler code generated before GH-5224 calls the two-argument overload, which must keep working
        // and keep its GetType() fallback
        using var host = await startHostAsync(typeof(CausationNamingCommandHandler));
        var runtime = host.GetRuntime();

        var handler = runtime.Handlers.HandlerFor<CausationNamingCommand>().ShouldBeAssignableTo<MessageHandler>()!;

        var context = new MessageContext(runtime);
        context.ReadEnvelope(new Envelope(new CausationNamingCommand("legacy")), InvocationCallback.Instance);
        await context.EnqueueCascadingAsync(new CausationNamingEvent("legacy"));
        context.Outstanding.ShouldNotBeEmpty();

        var observer = new CausationCapturingObserver();
        handler.RecordCauseAndEffect(context, observer);

        var caused = observer.Caused.ShouldHaveSingleItem();
        caused.Outgoing.ShouldBe(typeof(CausationNamingEvent).FullName);
        caused.Handler.ShouldBe(handler.GetType().FullName);
    }
}

public record CausationNamingCommand(string Text);

public record CausationNamingEvent(string Text);

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class CausationNamingCommandHandler
{
    public CausationNamingEvent Handle(CausationNamingCommand command) => new(command.Text);
}

// Hidden from conventional discovery so it cannot leak into every other host in this assembly
[WolverineIgnore]
public class SecondCausationNamingCommandHandler
{
    public void Handle(CausationNamingCommand command)
    {
    }
}

// Gives the cascaded event a route, so it is buffered into MessageContext.Outstanding
[WolverineIgnore]
public class CausationNamingEventHandler
{
    public void Handle(CausationNamingEvent message)
    {
    }
}

internal sealed record CausedEdge(string Incoming, string Outgoing, string Handler, string? Endpoint);

internal class CausationCapturingObserver : IWolverineObserver
{
    private readonly ConcurrentQueue<CausedEdge> _caused = new();

    public IReadOnlyList<CausedEdge> Caused => _caused.ToArray();

    public void MessageCausedBy(string incomingMessageType, string outgoingMessageType, string handlerType,
        string? endpointUri)
    {
        _caused.Enqueue(new CausedEdge(incomingMessageType, outgoingMessageType, handlerType, endpointUri));
    }

    public Task AssumedLeadership() => Task.CompletedTask;
    public Task NodeStarted() => Task.CompletedTask;
    public Task NodeStopped() => Task.CompletedTask;
    public Task AgentStarted(Uri agentUri) => Task.CompletedTask;
    public Task AgentStopped(Uri agentUri) => Task.CompletedTask;
    public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
    public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
    public Task RuntimeIsFullyStarted() => Task.CompletedTask;
    public void EndpointAdded(Endpoint endpoint) { }
    public void MessageRouted(Type messageType, IMessageRouter router) { }
    public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) => Task.CompletedTask;
    public Task BackPressureLifted(Endpoint endpoint) => Task.CompletedTask;
    public Task ListenerLatched(Endpoint endpoint) => Task.CompletedTask;
    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) => Task.CompletedTask;
    public Task CircuitBreakerReset(Endpoint endpoint) => Task.CompletedTask;
    public void PersistedCounts(Uri storeUri, PersistedCounts counts) { }
    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) { }
}
