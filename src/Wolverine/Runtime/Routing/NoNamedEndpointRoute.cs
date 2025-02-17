using JasperFx.Core;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime.Routing;

internal class NoNamedEndpointRoute : IMessageRoute
{
    private readonly string _message;

    public NoNamedEndpointRoute(string endpointName, string[] allNames)
    {
        EndpointName = endpointName;

        var nameList = allNames.Join(", ");
        _message = $"Endpoint name '{endpointName}' is invalid. Known endpoints are {nameList}";
    }

    public string EndpointName { get; }

    public Envelope CreateForSending(object message, DeliveryOptions? options, ISendingAgent localDurableQueue,
        WolverineRuntime runtime, string? topicName)
    {
        throw new UnknownEndpointException(_message);
    }

    public MessageSubscriptionDescriptor Describe()
    {
        throw new NotSupportedException();
    }

    public Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        throw new InvalidOperationException($"No endpoint with name '{EndpointName}'");
    }
}