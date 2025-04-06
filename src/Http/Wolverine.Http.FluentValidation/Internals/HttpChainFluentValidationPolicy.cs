using FluentValidation;
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
        foreach (var chain in chains.Where(x => x.HasRequestType || x.QueryStringObjectVariables.Any()))
        {
            Apply(chain, container);
        }
    }

    public void Apply(HttpChain chain, IServiceContainer container)
    {
        var types = chain.QueryStringObjectVariables
            .Select(x => x.VariableType)
            .ToList();

        if (chain.HasRequestType)
        {
            types.Add(chain.RequestType);
        }

        foreach (var type in types)
        {
            var validatorInterface = typeof(IValidator<>).MakeGenericType(type);

            var registered = container.RegistrationsFor(validatorInterface);

            if (registered.Count() == 1)
            {
                chain.Metadata.ProducesProblem(400);

                var method =
                    typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteOne))!
                        .MakeGenericMethod(type);

                var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method)
                {
                    CommentText = "Execute FluentValidation validators"
                };

                var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
                chain.Middleware.InsertRange(0, new Frame[] { methodCall, maybeResult });
            }
            else if (registered.Count() > 1)
            {
                chain.Metadata.ProducesProblem(400);

                var method =
                    typeof(FluentValidationHttpExecutor).GetMethod(nameof(FluentValidationHttpExecutor.ExecuteMany))!
                        .MakeGenericMethod(type);

                var methodCall = new MethodCall(typeof(FluentValidationHttpExecutor), method);
                var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
                chain.Middleware.InsertRange(0, new Frame[] { methodCall, maybeResult });
            }
        }
    }
}