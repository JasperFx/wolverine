using System.Diagnostics.CodeAnalysis;
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

    // chain.MessageType is the user-defined message type that handler discovery
    // already roots. The MakeGenericType (IValidator<T>) + MakeGenericMethod
    // (ExecuteOne<T> / ExecuteMany<T>) pair here is the standard chunk D / I /
    // J / K / AK codegen-time pattern: AOT consumers in TypeLoadMode.Static
    // pre-generate the closed Validate<T> call sites at codegen time, so the
    // reflective close never fires in steady state.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MakeGenericType/MakeGenericMethod close FluentValidationExecutor.ExecuteOne<T>/ExecuteMany<T> over a handler-rooted message type at codegen time; AOT consumers pre-generate via TypeLoadMode.Static. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType/MakeGenericMethod close FluentValidationExecutor.ExecuteOne<T>/ExecuteMany<T> over a handler-rooted message type at codegen time; AOT consumers pre-generate via TypeLoadMode.Static. See AOT guide / #2769.")]
    public void Apply(HandlerChain chain, IServiceContainer container)
    {
        var validatorInterface = typeof(IValidator<>).MakeGenericType(chain.MessageType);

        var registered = container.RegistrationsFor(validatorInterface);
        if (registered.Count() == 1)
        {
            var method = typeof(FluentValidationExecutor).GetMethod(nameof(FluentValidationExecutor.ExecuteOne))!
                .MakeGenericMethod(chain.MessageType);

            var methodCall = new MethodCall(typeof(FluentValidationExecutor), method);
            chain.Middleware.Add(methodCall);
        }
        else if (registered.Count() > 1)
        {
            var method = typeof(FluentValidationExecutor).GetMethod(nameof(FluentValidationExecutor.ExecuteMany))!
                .MakeGenericMethod(chain.MessageType);

            var methodCall = new MethodCall(typeof(FluentValidationExecutor), method);
            chain.Middleware.Add(methodCall);
        }
    }
}