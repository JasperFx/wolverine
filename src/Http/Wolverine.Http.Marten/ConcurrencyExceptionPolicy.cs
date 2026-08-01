using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Marten;
using Wolverine.Marten.Persistence.Sagas;
using Wolverine.Middleware;

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
    private static readonly string[] _bodylessHttpMethods = ["GET", "HEAD"];

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
        foreach (var chain in chains)
        {
            var handlings = aggregateHandlingFor(chain);
            if (handlings.Count == 0 && !chain.Postprocessors.OfType<DocumentSessionSaveChanges>().Any())
            {
                continue;
            }

            var tryCatchFinally = chain.GetOrCreateTryCatchFinallyFrame();

            // Also covers Marten's EventStreamUnexpectedMaxEventIdException and DcbConcurrencyException
            var applied = tryAddCatchBlock(tryCatchFinally, typeof(ConcurrencyException));

            // StreamLockedException is only ever thrown by the exclusive locking load path
            // (FetchForExclusiveWriting), and does *not* inherit from ConcurrencyException
            if (handlings.Any(x => x.LoadStyle == ConcurrencyStyle.Exclusive))
            {
                applied |= tryAddCatchBlock(tryCatchFinally, typeof(StreamLockedException));
            }

            // Alter the OpenAPI metadata to register the ProblemDetails path, but only where the
            // conflict is actually reachable in practice. A read-only GET that merely commits an
            // empty session (e.g. [ReadAggregate] under AutoApplyTransactions) keeps the safety-net
            // catch without advertising an unreachable conflict response to generated clients
            if (applied && (handlings.Count > 0 || chain.HttpMethods.Any(x => !_bodylessHttpMethods.Contains(x))))
            {
                chain.Metadata.ProducesProblem(_statusCode);
            }
        }
    }

    private bool tryAddCatchBlock(TryCatchFinallyFrame tryCatchFinally, Type exceptionType)
    {
        // A user-defined OnException handler for the same exception type has already claimed
        // the catch, and a second catch of the same type would not even compile (CS0160)
        if (tryCatchFinally.CatchBlocks.Any(x => x.ExceptionType == exceptionType))
        {
            return false;
        }

        tryCatchFinally.AddCatchBlock(exceptionType,
            [new RespondWithConcurrencyProblemDetailsFrame(exceptionType, _statusCode)]);
        return true;
    }

    // AggregateHandling.Store keeps a single instance until a second stream on the same chain
    // promotes the tag to a list, and AggregateHandling.TryLoad only reads the single shape,
    // so read both shapes here to catch exclusive loading on multi-stream chains too
    private static IReadOnlyList<AggregateHandling> aggregateHandlingFor(HttpChain chain)
    {
        if (chain.Tags.TryGetValue(nameof(AggregateHandling), out var raw))
        {
            if (raw is AggregateHandling single)
            {
                return [single];
            }

            if (raw is List<AggregateHandling> list)
            {
                return list;
            }
        }

        return [];
    }

    // Make the codegen easier by doing most of the work in this one method
    public static Task RespondWithProblemDetails(Exception e, HttpContext context, int statusCode)
    {
        // An escaping exception used to leave an error log behind, so keep a signal
        // for operators now that the exception stops here
        context.RequestServices.GetService<ILoggerFactory>()
            ?.CreateLogger(typeof(ConcurrencyExceptionPolicy))
            .LogInformation("Handled {ExceptionType} on {Method} {Path} as HTTP {StatusCode}",
                e.GetType().Name, context.Request.Method, context.Request.Path, statusCode);

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
        // Once the response has started streaming there is no way left to communicate the
        // failure in the body, so rethrow to preserve the connection-abort behavior instead
        // of quietly truncating a 2xx response
        writer.Write($"BLOCK:if ({_httpContext!.Usage}.{nameof(HttpContext.Response)}.{nameof(HttpResponse.HasStarted)})");
        writer.Write("throw;");
        writer.FinishBlock();

        writer.Write(
            $"await {typeof(ConcurrencyExceptionPolicy).FullNameInCode()}.{nameof(ConcurrencyExceptionPolicy.RespondWithProblemDetails)}({_exception!.Usage}, {_httpContext.Usage}, {_statusCode});");
        writer.Write("return;");
    }
}
