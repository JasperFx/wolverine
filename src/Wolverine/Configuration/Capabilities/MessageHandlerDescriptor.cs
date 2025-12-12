using JasperFx.Descriptors;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration.Capabilities;

public class MessageHandlerDescriptor : OptionsDescription
{
    public MessageHandlerDescriptor()
    {
    }

    public MessageHandlerDescriptor(HandlerChain chain, HandlerGraph handlers) : base(chain)
    {
        // TODO -- get error handling too
        foreach (var methodCall in chain.Handlers)
        {
            Handlers.Add(new HandlerMethod(TypeDescriptor.For(methodCall.HandlerType), methodCall.Method.Name));
        }

        CodeFileName = chain.TypeName;

        StickyEndpoints = chain.Endpoints.Select(x => x.Uri).ToArray();
    }

    // TODO -- use this later to retrieve a preview of the source code
    public string CodeFileName { get; set; }
    public Uri[] StickyEndpoints { get; set; } = [];

    public List<HandlerMethod> Handlers { get; set; } = new();
}