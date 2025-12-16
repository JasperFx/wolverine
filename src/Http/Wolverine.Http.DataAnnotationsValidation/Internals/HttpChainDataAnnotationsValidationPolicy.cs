using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.CodeGen;

namespace Wolverine.Http.DataAnnotationsValidation.Internals;

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