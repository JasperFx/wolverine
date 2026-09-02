using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
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

        // GH-4238, the message handler half. #4244 fixed this for HTTP chains and stopped there,
        // because nothing ran Wolverine.DataAnnotationsValidation.Tests -- the extension test projects
        // were outside every CI workflow, so six failing tests sat on main saying so.
        //
        // Same defect, same shape: Validate<T> takes an IServiceProvider to build the
        // ValidationContext, and an unsupplied one goes through the service container and is reported
        // to ServiceLocationPolicy. Under the 6.0 default of NotAllowed that made Wolverine's own
        // validation middleware unusable on a message handler at all -- the application threw
        // InvalidServiceLocationException at bootstrap.
        //
        // context.Runtime.Services is the right provider for the same reasons the HTTP twin uses
        // httpContext.RequestServices: the only thing the ValidationContext's provider ever does is
        // answer GetService for a ValidationAttribute or an IValidatableObject, and this runs before
        // any handler scope is relevant. "context" is safe to name directly -- it is the generated
        // HandleAsync parameter, and ContextVariable.OverrideName is a deliberate no-op so that any
        // other frame wanting the name gets renamed instead.
        methodCall.TrySetArgument(new Variable(typeof(IServiceProvider), "context.Runtime.Services"));

        chain.Middleware.Add(methodCall);
    }
}