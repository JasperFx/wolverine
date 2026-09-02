using System.ComponentModel.DataAnnotations;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
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
        if (!validatedType.GetProperties().Any(x => x.GetAllAttributes<ValidationAttribute>().Any()) && !validatedType.CanBeCastTo<IValidatableObject>())
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

        // GH-4238: supply the IServiceProvider ourselves rather than letting the codegen source it.
        // GH-4171 deliberately stopped IServiceProvider being answered as a derived HttpContext
        // variable, so an unsupplied one now goes through the service container and is reported to
        // ServiceLocationPolicy -- correct for user code, but it meant Wolverine's own validation
        // middleware could not be used at all under the 6.0 default of NotAllowed.
        //
        // HttpContext.RequestServices is the right provider here regardless of ServiceProviderSource:
        // the only thing the ValidationContext's provider does is answer GetService for a
        // ValidationAttribute or IValidatableObject, this runs against the inbound request before any
        // handler scope is relevant, and it is the same provider ASP.NET Core's own model validation
        // hands to a ValidationContext.
        methodCall.TrySetArgument(
            new Variable(typeof(IServiceProvider), $"httpContext.{nameof(HttpContext.RequestServices)}"));

        var maybeResult = new MaybeEndWithResultFrame(methodCall.ReturnVariable!);
        chain.Middleware.InsertRange(0, [methodCall, maybeResult]);
    }
}