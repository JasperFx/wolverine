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
        foreach (var chain in chains.Where(x => x.HasRequestType))
        {
            Apply(chain, container);
        }
    }

    public void Apply(HttpChain chain, IServiceContainer container)
    {
        var validatorInterface = typeof(IValidator<>).MakeGenericType(chain.RequestType!);

        var registered = container.RegistrationsFor(validatorInterface);

        if (registered.Count() == 1)
        {
            chain.Metadata.ProducesValidationProblem(400);

            var method =
                typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteOne))!
                    .MakeGenericMethod(chain.RequestType);

            var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method)
            {
                CommentText = "Execute FluentValidation validators"
            };

            var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
            chain.Middleware.InsertRange(0, new Frame[]{methodCall,maybeResult});
        }
        else if (registered.Count() > 1)
        {
            chain.Metadata.ProducesValidationProblem(400);

            var method =
                typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteMany))!
                    .MakeGenericMethod(chain.RequestType);

            var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method);
            var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
            chain.Middleware.InsertRange(0, new Frame[]{methodCall,maybeResult});
        }
    }
}