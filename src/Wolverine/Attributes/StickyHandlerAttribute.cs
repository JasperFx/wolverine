using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

/// <summary>
/// Marking a handler method or handler type with this attribute tells
/// Wolverine that this handler only applies to that listening endpoint.
/// This is helpful if you need to handle the same message type differently
/// from different endpoints. The endpoint name can also apply to local queues
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class StickyHandlerAttribute : Attribute
{
    public string EndpointName { get; }

    public StickyHandlerAttribute(string endpointName)
    {
        EndpointName = endpointName;
    }
}