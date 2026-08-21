using System.Diagnostics.CodeAnalysis;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Wolverine.Configuration.EventModeling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration.Capabilities;

public class MessageHandlerDescriptor : OptionsDescription
{
    public MessageHandlerDescriptor()
    {
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "OptionsDescription(chain) reads chain.GetType().GetProperties() to build a diagnostic description. MessageHandlerDescriptor is a diagnostic surface (Capabilities reporting); HandlerChain properties trimmed away are silently omitted, which is acceptable.")]
    public MessageHandlerDescriptor(HandlerChain chain, HandlerGraph handlers) : base(chain)
    {
        // TODO -- get error handling too
        foreach (var methodCall in chain.Handlers)
        {
            Handlers.Add(new HandlerMethod(TypeDescriptor.For(methodCall.HandlerType), methodCall.Method.Name));
        }

        CodeFileName = chain.TypeName;

        StickyEndpoints = chain.Endpoints.Select(x => x.Uri).ToArray();

        // GH-3988: the chain's Event Modeling roles — command / handler / aggregates / emitted events /
        // read models / published messages — derived off the chain itself. Diagnostic: a chain the
        // reader cannot make sense of simply has no slice, the rest of the descriptor is unaffected.
        try
        {
            EventModel = EventModelRoles.ForHandlerChain(chain);
        }
        catch (Exception)
        {
            EventModel = null;
        }
    }

    // TODO -- use this later to retrieve a preview of the source code
    public string CodeFileName { get; set; } = null!;
    public Uri[] StickyEndpoints { get; set; } = [];

    public List<HandlerMethod> Handlers { get; set; } = new();

    /// <summary>
    ///     The Event Modeling slice this handler chain implements (GH-3988): the inbound message as the
    ///     command, the handler, the aggregate(s) it decides against, the events it emits, the read models
    ///     it loads and the messages it cascades — derived from the chain, never declared. Null only when
    ///     the roles could not be read.
    /// </summary>
    public EventModelSliceDescriptor? EventModel { get; set; }
}