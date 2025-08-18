using FluentValidation;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.FluentValidation.Internals;

internal class FluentValidationPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains) Apply(chain, container);
    }

    public void Apply(HandlerChain chain, IServiceContainer container)
    {
        var validatorInterface = typeof(IValidator<>).MakeGenericType(chain.MessageType);

        var registered = container.RegistrationsFor(validatorInterface);
        if (registered.Count() == 1)
        {
            var method = typeof(FluentValidationExecutor).GetMethod(nameof(FluentValidationExecutor.ExecuteOne))
                .MakeGenericMethod(chain.MessageType);

            var methodCall = new MethodCall(typeof(FluentValidationExecutor), method);
            chain.Middleware.Add(methodCall);
        }
        else if (registered.Count() > 1)
        {
            var method = typeof(FluentValidationExecutor).GetMethod(nameof(FluentValidationExecutor.ExecuteMany))
                .MakeGenericMethod(chain.MessageType);

            var methodCall = new MethodCall(typeof(FluentValidationExecutor), method);
            chain.Middleware.Add(methodCall);
        }
    }
}