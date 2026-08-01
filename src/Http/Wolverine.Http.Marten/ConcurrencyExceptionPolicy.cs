using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Marten;
using Wolverine.Marten.Persistence.Sagas;

namespace Wolverine.Http.Marten;

/// <summary>
///     Opt-in policy that responds with a ProblemDetails body and a configurable status code
///     (409 Conflict by default) when a Marten optimistic concurrency check fails on an HTTP
///     endpoint using the aggregate handler workflow or Marten transactional middleware, instead
///     of letting the exception escape as a 500. Register this policy through
///     <see cref="WolverineHttpOptionsExtensions.UseProblemDetailsForConcurrencyExceptions" />
/// </summary>
public class ConcurrencyExceptionPolicy : IHttpPolicy
{
    private static readonly Type[] _exceptionTypes =
    [
        // Also covers Marten's EventStreamUnexpectedMaxEventIdException and DcbConcurrencyException
        typeof(ConcurrencyException),

        // Thrown on the load path by FetchForExclusiveWriting when the stream is already locked,
        // and does *not* inherit from ConcurrencyException
        typeof(StreamLockedException)
    ];

    private readonly int _statusCode;

    public ConcurrencyExceptionPolicy(int statusCode = 409)
    {
        _statusCode = statusCode;
    }

    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        // Find *only* the HTTP routes that could plausibly throw a Marten concurrency exception:
        // the aggregate handler workflow (FetchForWriting), and any route that commits a Marten
        // session through the transactional middleware
        foreach (var chain in chains.Where(shouldApply))
        {
            var tryCatchFinally = chain.GetOrCreateTryCatchFinallyFrame();
            var applied = false;

            foreach (var exceptionType in _exceptionTypes)
            {
                // A user-defined OnException handler for the same exception type has already claimed
                // the catch, and a second catch of the same type would not even compile (CS0160)
                if (tryCatchFinally.CatchBlocks.Any(x => x.ExceptionType == exceptionType))
                {
                    continue;
                }

                tryCatchFinally.AddCatchBlock(exceptionType,
                    [new RespondWithConcurrencyProblemDetailsFrame(exceptionType, _statusCode)]);
                applied = true;
            }

            if (applied)
            {
                // Alter the OpenAPI metadata to register the ProblemDetails path
                chain.Metadata.ProducesProblem(_statusCode);
            }
        }
    }

    private static bool shouldApply(HttpChain chain)
    {
        return AggregateHandling.TryLoad(chain, out _)
               || chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any();
    }

    // Make the codegen easier by doing most of the work in this one method
    public static Task RespondWithProblemDetails(Exception e, HttpContext context, int statusCode)
    {
        // The concurrency exception may surface after the endpoint has already begun streaming
        // a response, in which case there is no way left to communicate the failure in the body
        if (context.Response.HasStarted)
        {
            return Task.CompletedTask;
        }

        var problems = new ProblemDetails
        {
            Title = "Concurrency conflict",
            Detail = e.Message,
            Status = statusCode
        };

        return Results.Problem(problems).ExecuteAsync(context);
    }
}

// This is the actual code being injected into a catch block of the
// runtime code generation
internal class RespondWithConcurrencyProblemDetailsFrame : AsyncFrame
{
    private readonly Type _exceptionType;
    private readonly int _statusCode;
    private Variable? _exception;
    private Variable? _httpContext;

    public RespondWithConcurrencyProblemDetailsFrame(Type exceptionType, int statusCode)
    {
        _exceptionType = exceptionType;
        _statusCode = statusCode;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Resolved from the enclosing catch block's exception variable
        _exception = chain.FindVariable(_exceptionType);
        yield return _exception;

        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {typeof(ConcurrencyExceptionPolicy).FullNameInCode()}.{nameof(ConcurrencyExceptionPolicy.RespondWithProblemDetails)}({_exception!.Usage}, {_httpContext!.Usage}, {_statusCode});");
        writer.Write("return;");
    }
}
