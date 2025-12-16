using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.DataAnnotationsValidation.Internals;

public class DataAnnotationsValidationPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains) Apply(chain, container);
    }

    public void Apply(HandlerChain chain, IServiceContainer container)
    {
        var method =
            typeof(DataAnnotationsValidationExecutor).GetMethod(nameof(DataAnnotationsValidationExecutor.Validate))!
                .MakeGenericMethod(chain.MessageType!);

        var methodCall = new MethodCall(typeof(DataAnnotationsValidationExecutor), method)
        {
            CommentText = "Execute DataAnnotations validation"
        };
        chain.Middleware.Add(methodCall);
    }
}