using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Lamar;
using Marten.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

public class StreamCollisionExceptionPolicy : IHttpPolicy
{
    private bool shouldApply(HttpChain chain)
    {
        // TODO -- and Wolverine needs a utility method on IChain to make this declarative
        // for future middleware construction
        return chain
            .HandlerCalls()
            .SelectMany(x => x.Creates)
            .Any(x => x.VariableType.CanBeCastTo<IStartStream>());
    }
    
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains.Where(shouldApply))
        {
            // Add the middleware on the outside
            chain.Middleware.Insert(0, new CatchStreamCollisionFrame());
            
            // Alter the OpenAPI metadata to register the ProblemDetails
            // path
            chain.Metadata.ProducesProblem(400);
        }
    }

    public static Task RespondWithProblemDetails(ExistingStreamIdCollisionException e, HttpContext context)
    {
        var problems = new ProblemDetails
        {
            Detail = $"Duplicated id '{e.Id}'",
            Extensions =
            {
                ["Id"] = e.Id
            },
            Status = 400 // The default is 500, so watch this
        };

        return Results.Problem(problems).ExecuteAsync(context);
    }
}

internal class CatchStreamCollisionFrame : AsyncFrame
{
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("BLOCK:try");
        
        // Write the inner code here
        Next?.GenerateCode(method, writer);
        
        writer.FinishBlock();
        writer.Write($@"
BLOCK:catch({typeof(ExistingStreamIdCollisionException).FullNameInCode()} e)
await {typeof(StreamCollisionExceptionPolicy).FullNameInCode()}.{nameof(StreamCollisionExceptionPolicy.RespondWithProblemDetails)}(e, httpContext);
return;
END

");
    }

}