using FluentValidation;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.CodeGen;
using IContainer = Lamar.IContainer;

namespace Wolverine.Http.FluentValidation.Internals;

internal class HttpChainFluentValidationPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains.Where(x => x.RequestType != null)) Apply(chain, container);
    }

    public void Apply(HttpChain chain, IContainer container)
    {
        var validatorInterface = typeof(IValidator<>).MakeGenericType(chain.RequestType!);

        var registered = container.Model.For(validatorInterface);
        
        if (registered.Instances.Count() == 1)
        {
            chain.Metadata.ProducesProblem(400);

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
        else if (registered.Instances.Count() > 1)
        {
            chain.Metadata.ProducesProblem(400);

            var method =
                typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteMany))!
                    .MakeGenericMethod(chain.RequestType);

            var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method);
            var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
            chain.Middleware.InsertRange(0, new Frame[]{methodCall,maybeResult});
        }
    }
}