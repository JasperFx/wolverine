using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Http.Tests;

public record CausePing(string Note);

public record CauseDoc(System.Guid Id);

// A discovered local handler so the published CausePing has a route (without it, PublishAsync finds
// no route, buffers nothing into Outstanding, and the endpoint-causation frame sees no outgoing
// message). Instance (non-static) so Discovery.IncludeType can register it under
// DisableConventionalDiscovery. Body irrelevant — the test asserts the causation, not the handling.
public class CausePingHandler
{
    public void Handle(CausePing _) { }
}

public static class CauseEndpoint
{
    // Writing through IDocumentSession enrolls the endpoint in the Marten/Wolverine outbox
    // (AutoApplyTransactions), so the published message buffers into MessageContext.Outstanding —
    // exactly what the endpoint-causation frame reads, mirroring a durable handler.
    [WolverinePost("/cause/publish")]
    public static async Task Publish(IDocumentSession session, IMessageBus bus)
    {
        session.Store(new CauseDoc(System.Guid.NewGuid()));
        await bus.PublishAsync(new CausePing("from-endpoint"));
    }
}

// CritterWatch #396 Phase 4 item 5. HTTP endpoints are not MessageHandler subclasses, so
// MessageHandler.RecordCauseAndEffect never runs for them and a message published from an endpoint
// has no observed cause (RecordCauseAndEffect keys on the incoming message type, of which an endpoint
// has none). When WolverineOptions.Tracking.EnableMessageCausationTracking is on, HttpChain codegen
// emits a RecordEndpointCausationFrame that attributes each published message to the endpoint origin
// (verb + route) via EndpointCausation.RecordEndpointCauseAndEffect, so a custom IWolverineObserver
// (CritterWatch's observed-causation graph) can see endpoint-originated publishes.
public class endpoint_message_causation_tracking
{
    private sealed record Caused(string Incoming, string Outgoing, string Handler, string? Endpoint);

    private sealed class CapturingObserver : IWolverineObserver
    {
        public ConcurrentBag<Caused> Pairs { get; } = new();

        public Task AssumedLeadership() => Task.CompletedTask;
        public Task NodeStarted() => Task.CompletedTask;
        public Task NodeStopped() => Task.CompletedTask;
        public Task AgentStarted(System.Uri agentUri) => Task.CompletedTask;
        public Task AgentStopped(System.Uri agentUri) => Task.CompletedTask;
        public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands) => Task.CompletedTask;
        public Task StaleNodes(System.Collections.Generic.IReadOnlyList<WolverineNode> staleNodes) => Task.CompletedTask;
        public Task RuntimeIsFullyStarted() => Task.CompletedTask;
        public void EndpointAdded(Wolverine.Configuration.Endpoint endpoint) { }
        public void MessageRouted(System.Type messageType, Wolverine.Runtime.Routing.IMessageRouter router) { }
        public Task BackPressureTriggered(Wolverine.Configuration.Endpoint endpoint, Wolverine.Transports.IListeningAgent agent) => Task.CompletedTask;
        public Task BackPressureLifted(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public Task ListenerLatched(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public Task CircuitBreakerTripped(Wolverine.Configuration.Endpoint endpoint, Wolverine.ErrorHandling.CircuitBreakerOptions options) => Task.CompletedTask;
        public Task CircuitBreakerReset(Wolverine.Configuration.Endpoint endpoint) => Task.CompletedTask;
        public void PersistedCounts(System.Uri storeUri, Wolverine.Logging.PersistedCounts counts) { }
        public void MessageHandlingMetricsExported(Wolverine.Runtime.Metrics.MessageHandlingMetrics metrics) { }

        public void MessageCausedBy(string incomingMessageType, string outgoingMessageType, string handlerType, string? endpointUri)
            => Pairs.Add(new Caused(incomingMessageType, outgoingMessageType, handlerType, endpointUri));
    }

    private static async Task<IAlbaHost> buildHostAsync(bool trackingEnabled)
    {
        var schema = "cause_" + System.Guid.NewGuid().ToString("N")[..8];
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseWolverine(opts =>
        {
            opts.Tracking.EnableMessageCausationTracking = trackingEnabled;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType<CausePingHandler>();
            opts.Discovery.IncludeAssembly(typeof(endpoint_message_causation_tracking).Assembly);
            opts.Policies.AutoApplyTransactions();
            opts.Policies.UseDurableLocalQueues();

            opts.Services.AddMarten(m =>
            {
                m.Connection(Servers.PostgresConnectionString);
                m.DatabaseSchemaName = schema;
            }).IntegrateWithWolverine();
        });

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints());
    }

    [Fact]
    public async Task codegen_emits_endpoint_causation_call_when_tracking_enabled()
    {
        await using var host = await buildHostAsync(trackingEnabled: true);

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/cause/publish");
        chain.ShouldNotBeNull();
        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);

        var source = chain!.SourceCode;
        source.ShouldNotBeNull();
        source.ShouldContain("EndpointCausation.RecordEndpointCauseAndEffect");
        // The origin (verb + route) is rendered as the stand-in for the absent incoming message type.
        source.ShouldContain("\"POST /cause/publish\"");
    }

    [Fact]
    public async Task codegen_omits_endpoint_causation_call_when_tracking_disabled()
    {
        await using var host = await buildHostAsync(trackingEnabled: false);

        var graph = host.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!;
        var chain = graph.ChainFor("POST", "/cause/publish");
        chain.ShouldNotBeNull();
        chain.As<ICodeFile>().InitializeSynchronously(graph.Rules, graph, host.Services);

        chain!.SourceCode.ShouldNotBeNull();
        chain.SourceCode.ShouldNotContain("EndpointCausation");
    }

    [Fact]
    public async Task observer_is_notified_of_endpoint_originated_publish_at_runtime()
    {
        await using var host = await buildHostAsync(trackingEnabled: true);

        var observer = new CapturingObserver();
        host.Services.GetRequiredService<IWolverineRuntime>().Observer = observer;

        await host.Scenario(x =>
        {
            x.Post.Url("/cause/publish");
            x.StatusCodeShouldBe(204);
        });

        var pair = observer.Pairs.SingleOrDefault(p => p.Outgoing.Contains(nameof(CausePing)));
        pair.ShouldNotBeNull();
        pair!.Incoming.ShouldBe("POST /cause/publish");
    }
}
