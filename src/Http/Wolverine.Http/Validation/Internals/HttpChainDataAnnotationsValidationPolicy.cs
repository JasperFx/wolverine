using System.ComponentModel.DataAnnotations;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.Validation.Internals;

internal class HttpChainDataAnnotationsValidationPolicy : IHttpPolicy
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

        // ONLY apply if there are ValidationAttributes
        if (!validatedType.GetProperties().Any(x => x.GetAllAttributes<ValidationAttribute>().Any()))
        {
            return;
        }
        
        chain.Metadata.ProducesValidationProblem();

        var method =
            typeof(DataAnnotationsHttpValidationExecutor).GetMethod(nameof(DataAnnotationsHttpValidationExecutor.Validate))!
                .MakeGenericMethod(validatedType);

        var methodCall = new MethodCall(typeof(DataAnnotationsHttpValidationExecutor), method)
            {
                CommentText = "Execute DataAnnotation validation"
            };

        var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
        chain.Middleware.InsertRange(0, [methodCall, maybeResult]);
    }
}