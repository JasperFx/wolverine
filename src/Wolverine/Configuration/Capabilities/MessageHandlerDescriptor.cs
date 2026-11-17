using System.Diagnostics.CodeAnalysis;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Wolverine.Configuration.EventModeling;
using Wolverine.Persistence.Sagas;
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
        // GH-5139. A handler chain belongs to exactly one handler type in Wolverine 7
        var handlerType = chain is SagaChain { SagaType: { } sagaType }
            ? sagaType
            : chain.HandlerCalls().FirstOrDefault()?.HandlerType;
        HandlerType = handlerType == null ? null : TypeDescriptor.For(handlerType);

        // TODO -- get error handling too
        // A saga chain clears its handler calls once it has generated code, so read its sorted calls as well
        var calls = chain is SagaChain sagaChain ? sagaChain.SagaHandlerCalls() : chain.Handlers.ToArray();
        foreach (var methodCall in calls)
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

    /// <summary>
    ///     GH-5139. The one handler type this handler chain belongs to: the handler class, or the saga type for
    ///     a saga. Wolverine 7 builds one handler chain per (message type, handler type), so a message type
    ///     handled by several handler types has one <see cref="MessageHandlerDescriptor"/> per handler type.
    ///     Null when read from a Wolverine 6 service, which did not send it
    /// </summary>
    public TypeDescriptor? HandlerType { get; set; }

    /// <summary>
    ///     The handler methods of this chain. All of them belong to <see cref="HandlerType"/>; there is more than
    ///     one only for a saga (its Start, Handle and NotFound methods for the same message)
    /// </summary>
    public List<HandlerMethod> Handlers { get; set; } = new();

    /// <summary>
    ///     The Event Modeling slice this handler chain implements (GH-3988): the inbound message as the
    ///     command, the handler, the aggregate(s) it decides against, the events it emits, the read models
    ///     it loads and the messages it cascades — derived from the chain, never declared. Null only when
    ///     the roles could not be read.
    /// </summary>
    public EventModelSliceDescriptor? EventModel { get; set; }
}