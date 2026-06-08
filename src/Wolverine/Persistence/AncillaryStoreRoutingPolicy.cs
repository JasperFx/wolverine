using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Util;

namespace Wolverine.Persistence;

internal sealed class AncillaryStoreRoutingPolicy : IHandlerPolicy, IEnvelopeRule
{
    private readonly Type _storeMarkerType;
    private readonly Type[] _messageTypes;
    private readonly HashSet<string> _messageTypeNames;

    public AncillaryStoreRoutingPolicy(Type storeMarkerType, Type[] messageTypes)
    {
        _storeMarkerType = storeMarkerType ?? throw new ArgumentNullException(nameof(storeMarkerType));

        if (messageTypes == null || messageTypes.Length == 0)
        {
            throw new ArgumentException("At least one message type is required", nameof(messageTypes));
        }

        _messageTypes = messageTypes.Distinct().ToArray();
        if (_messageTypes.Any(x => x is null))
        {
            throw new ArgumentException("Message types cannot contain null values", nameof(messageTypes));
        }

        _messageTypeNames = _messageTypes.Select(x => x.ToMessageTypeName()).ToHashSet();
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            applyTo(chain);

            foreach (var byEndpoint in chain.ByEndpoint)
            {
                applyTo(byEndpoint);
            }
        }
    }

    private void applyTo(HandlerChain chain)
    {
        if (_messageTypes.Contains(chain.MessageType))
        {
            chain.AncillaryStoreType = _storeMarkerType;
        }
    }

    public void Modify(Envelope envelope)
    {
        // This rule needs the current runtime to resolve the enrolled ancillary
        // store, so the work happens in ApplyCorrelation().
    }

    public void ApplyCorrelation(IMessageContext originator, Envelope outgoing)
    {
        if (outgoing.MessageType == null || !_messageTypeNames.Contains(outgoing.MessageType))
        {
            return;
        }

        if (originator is MessageBus bus)
        {
            outgoing.Store = bus.Runtime.Stores.FindAncillaryStore(_storeMarkerType);
        }
    }

    public override string ToString()
    {
        return
            $"Route messages {nameof(_messageTypes)}: {_messageTypes.Select(x => x.FullNameInCode()).Join(", ")} to ancillary store {_storeMarkerType.FullNameInCode()}";
    }
}