using System.Diagnostics.CodeAnalysis;
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

    // chain.MessageType! is the user-defined message type that handler discovery
    // already roots. MakeGenericMethod here is the standard chunk D / I / J / K
    // codegen-time pattern: AOT consumers in TypeLoadMode.Static pre-generate
    // the closed Validate<T> call sites at codegen time, so the reflective
    // close never fires in steady state.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MakeGenericMethod closes DataAnnotationsValidationExecutor.Validate<T> over a handler-rooted message type at codegen time; AOT consumers pre-generate via TypeLoadMode.Static. See AOT guide / #2769.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericMethod closes DataAnnotationsValidationExecutor.Validate<T> over a handler-rooted message type at codegen time; AOT consumers pre-generate via TypeLoadMode.Static. See AOT guide / #2769.")]
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