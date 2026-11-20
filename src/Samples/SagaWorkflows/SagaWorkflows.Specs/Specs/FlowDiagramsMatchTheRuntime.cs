using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace SagaWorkflows.Specs;

// GH-5701: a saga's flow diagram draws a "fails" edge from each message a Compensate method covers. The runtime builds
// a Fault<T> chain for each message it compensates. Both read the same answer, so for every sample saga the drawn
// failure edges are exactly the concrete Fault<T> chains the runtime has for that saga. The trip's catch-all, for one,
// leaves out TripBooked: the trip sends it as it completes, so it is not a step that can be compensated
public static class FlowDiagramChecks
{
    public static async Task EveryFailureEdgeIsAFaultChainAsync(IHost host)
    {
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);

        var sagas = capabilities.Sagas.Where(x => x.Compensations.Count > 0).ToArray();
        sagas.ShouldNotBeEmpty();

        foreach (var saga in sagas)
        {
            var drawn = failureEdgesIn(SagaFlowDiagram.ToMermaid(saga));

            var faulted = ((WolverineRuntime)runtime).Handlers.Chains
                .OfType<SagaChain>()
                .Where(x => x.SagaType.FullName == saga.StateType.FullName)
                .Select(x => SagaCompensationsFaultOf(x.MessageType))
                .OfType<Type>()
                .Where(x => !x.IsInterface && !x.IsAbstract && x != typeof(object))
                .Select(x => x.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();

            drawn.ShouldBe(faulted, $"The failure edges drawn for {saga.StateType.Name}");
        }
    }

    // The names of the messages with a "fails" edge out of them, from the diagram's own node labels
    private static string[] failureEdgesIn(string mermaid)
    {
        var names = Regex.Matches(mermaid, "^\\s*(msg_\\w+)\\(\\[\"(.+?)\"\\]\\)", RegexOptions.Multiline)
            .ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);

        return Regex.Matches(mermaid, "^\\s*(msg_\\w+) -\\.->\\|\"fails\"\\|", RegexOptions.Multiline)
            .Select(x => names[x.Groups[1].Value])
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Type? SagaCompensationsFaultOf(Type messageType)
        => messageType.IsGenericType && messageType.GetGenericTypeDefinition() == typeof(Fault<>)
            ? messageType.GetGenericArguments()[0]
            : null;
}

[Collection(nameof(DocumentSagaCollection))]
public class document_saga_flow_diagrams_match_the_runtime(DocumentSagaServices services)
{
    [Fact]
    public Task every_failure_edge_is_a_fault_chain() => FlowDiagramChecks.EveryFailureEdgeIsAFaultChainAsync(services.Workflows);

    [Fact]
    public async Task the_trips_catch_all_leaves_out_the_message_it_sends_as_it_completes()
    {
        var runtime = services.Workflows.Services.GetRequiredService<IWolverineRuntime>();
        var capabilities = await ServiceCapabilities.ReadFrom(runtime, null, CancellationToken.None);
        var trip = capabilities.Sagas.Single(x => x.StateType.Name == nameof(TripBooking.Trip));

        var catchAll = trip.Compensations.Single(x => x.FailedMessageType.FullName == typeof(object).FullName);
        catchAll.Covers.Select(x => x.Name).ShouldBe(["BookCar", "BookFlight", "BookHotel"]);
    }
}

[Collection(nameof(EventSourcedSagaCollection))]
public class event_sourced_saga_flow_diagrams_match_the_runtime(EventSourcedSagaServices services)
{
    [Fact]
    public Task every_failure_edge_is_a_fault_chain() => FlowDiagramChecks.EveryFailureEdgeIsAFaultChainAsync(services.Workflows);
}

[Collection(nameof(DocumentTripThatSaysWhyCollection))]
public class tuple_trip_flow_diagrams_match_the_runtime(DocumentTripThatSaysWhyServices services)
{
    [Fact]
    public Task every_failure_edge_is_a_fault_chain() => FlowDiagramChecks.EveryFailureEdgeIsAFaultChainAsync(services.Workflows);
}

[Collection(nameof(EventSourcedTripThatSaysWhyCollection))]
public class event_sourced_tuple_trip_flow_diagrams_match_the_runtime(EventSourcedTripThatSaysWhyServices services)
{
    [Fact]
    public Task every_failure_edge_is_a_fault_chain() => FlowDiagramChecks.EveryFailureEdgeIsAFaultChainAsync(services.Workflows);
}
