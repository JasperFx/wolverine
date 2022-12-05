using FluentValidation;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Lamar;
using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.FluentValidation.Internals;

internal class FluentValidationPolicy : IHandlerPolicy
{
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        foreach (var chain in graph.Chains) Apply(chain, container);
    }

    public void Apply(HandlerChain chain, IContainer container)
    {
        var validatorInterface = typeof(IValidator<>).MakeGenericType(chain.MessageType);

        var registered = container.Model.For(validatorInterface);
        if (registered.Instances.Count() == 1)
        {
            var method = typeof(FluentValidationExecutor).GetMethod(nameof(FluentValidationExecutor.ExecuteOne))
                .MakeGenericMethod(chain.MessageType);

            var methodCall = new MethodCall(typeof(FluentValidationExecutor), method);
            chain.Middleware.Add(methodCall);
        }
        else if (registered.Instances.Count() > 1)
        {
            var method = typeof(FluentValidationExecutor).GetMethod(nameof(FluentValidationExecutor.ExecuteMany))
                .MakeGenericMethod(chain.MessageType);

            var methodCall = new MethodCall(typeof(FluentValidationExecutor), method);
            chain.Middleware.Add(methodCall);
        }
    }
}