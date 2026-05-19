using System.Diagnostics.CodeAnalysis;
using FluentValidation;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.CodeGen;
using Wolverine.Runtime;

namespace Wolverine.Http.FluentValidation.Internals;

internal class HttpChainFluentValidationPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            Apply(chain, container);
        }
    }

    public void Apply(HttpChain chain, IServiceContainer container)
    {
        var validatedType = chain.HasRequestType ? chain.RequestType : chain.ComplexQueryStringType;
        if (validatedType == null) return;

        addValidationMiddleware(chain, container, validatedType);

        // When using [AsParameters] with a [FromBody] property, the RequestType gets
        // overwritten to the body property type. We still want to validate the
        // AsParameters type itself if it has a validator registered.
        if (chain.AsParametersType != null && chain.AsParametersType != validatedType)
        {
            addValidationMiddleware(chain, container, chain.AsParametersType);
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType/MakeGenericMethod close FluentValidationHttpExecutor.ExecuteOne<T>/ExecuteMany<T> over the HTTP chain's request type at codegen time; AOT consumers pre-generate via TypeLoadMode.Static. Same pattern as Wolverine.FluentValidation's FluentValidationPolicy. See AOT guide / #2769.")]
    private static void addValidationMiddleware(HttpChain chain, IServiceContainer container, Type validatedType)
    {
        var validatorInterface = typeof(IValidator<>).MakeGenericType(validatedType);

        var registered = container.RegistrationsFor(validatorInterface);

        if (registered.Count() == 1)
        {
            chain.Metadata.ProducesValidationProblem(400);

            var method =
                typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteOne))!
                    .MakeGenericMethod(validatedType);

            var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method)
            {
                CommentText = "Execute FluentValidation validators"
            };

            var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
            chain.Middleware.InsertRange(0, [methodCall, maybeResult]);
        }
        else if (registered.Count() > 1)
        {
            chain.Metadata.ProducesValidationProblem(400);

            var method =
                typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteMany))!
                    .MakeGenericMethod(validatedType);

            var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method);
            var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
            chain.Middleware.InsertRange(0, [methodCall, maybeResult]);
        }
    }
}