using JasperFx.CodeGeneration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class StickyHandlerAttribute : Attribute
{
    public string EndpointName { get; }

    public StickyHandlerAttribute(string endpointName)
    {
        EndpointName = endpointName;
    }
}