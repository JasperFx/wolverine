using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Logging;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class TagHttpHandlerPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            chain.Middleware.Insert(0, new TagHttpHandlerFrame(chain));
        }
    }
}

internal class TagHttpHandlerFrame : SyncFrame
{
    // GH-5332: the OpenTelemetry semantic convention names, the same ones the ASP.NET Core instrumentation sets
    internal const string HttpRoute = "http.route";
    internal const string HttpRequestMethod = "http.request.method";

    private readonly HttpChain _chain;
    private MessageActivityVariable? _activity;
    private Variable? _httpContext;

    public TagHttpHandlerFrame(HttpChain chain)
    {
        _chain = chain;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // GH-5310: ASP.NET Core's span for the request rather than whatever Activity.Current has become
        _activity = MessageActivityVariable.TryFind(chain);
        foreach (var variable in MessageActivityVariable.Uses(_activity)) yield return variable;

        // A chain that accepts several methods tags the request's actual method, read from the HttpContext
        if (singleHttpMethod() == null)
        {
            _httpContext = chain.FindVariable(typeof(HttpContext));
            yield return _httpContext;
        }
    }

    private string? singleHttpMethod()
    {
        return _chain.HttpMethods.Count() == 1 ? _chain.HttpMethods.Single() : null;
    }

    // Read at codegen time rather than in the constructor, because the route prefix and API versioning
    // policies may rewrite the route pattern after this frame is created
    private string? routePattern()
    {
        return _chain.RoutePattern?.RawText;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // One chained statement, so the request span is looked up once
        var handlerTypeName = _chain.Method.HandlerType.FullNameInCode();
        writer.WriteLine(
            $"{MessageActivityVariable.UsageFor(_activity)}?{tags($"\"{handlerTypeName}\"", _httpContext?.Usage)};");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        var handlerTypeName = _chain.Method.HandlerType.FSharpName();
        var current = MessageActivityVariable.FSharpUsageFor(_activity);
        writer.Write(
            $"if not (isNull {current}) then {current}{tags($"\"{handlerTypeName}\"", _httpContext?.FSharpUsage)} |> ignore");

        Next?.GenerateFSharpCode(method, writer);
    }

    private string tags(string handlerTypeName, string? httpContextUsage)
    {
        var tags = $".{nameof(Activity.SetTag)}(\"{WolverineTracing.HandlerType}\", {handlerTypeName})";

        if (routePattern() is { } route)
        {
            tags += $".{nameof(Activity.SetTag)}(\"{HttpRoute}\", {literal(route)})";
        }

        return tags + $".{nameof(Activity.SetTag)}(\"{HttpRequestMethod}\", {httpMethod(httpContextUsage)})";
    }

    // A constant for a chain with one method, so the common case costs nothing per request
    private string httpMethod(string? httpContextUsage)
    {
        return singleHttpMethod() is { } single
            ? literal(single)
            : $"{httpContextUsage}.{nameof(HttpContext.Request)}.{nameof(HttpRequest.Method)}";
    }

    // A route pattern may hold characters that would end or escape a string literal, as in a regex
    // constraint. The escaping is the same in C# and F#.
    private static string literal(string value)
    {
        return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
