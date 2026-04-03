using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Resources;

namespace Wolverine.Http.ContentNegotiation;

/// <summary>
/// Resource writer policy that discovers WriteResponse/WriteResponseAsync methods
/// on the handler type and generates content-negotiated response writing code.
/// </summary>
internal class ContentNegotiationWriterPolicy : IResourceWriterPolicy
{
    public static readonly string[] WriteResponseMethodNames = ["WriteResponse", "WriteResponseAsync"];

    public bool TryApply(HttpChain chain)
    {
        if (!chain.HasResourceType()) return false;

        var handlerType = chain.Method.HandlerType;
        var writers = DiscoverWriteMethods(handlerType);

        if (writers.Count == 0) return false;

        var resourceVariable = chain.ResourceVariable ?? chain.Method.Creates.First();
        resourceVariable.OverrideName(resourceVariable.Usage + "_response");

        chain.Postprocessors.Add(new ContentNegotiationWriteFrame(resourceVariable, writers, chain.ConnegMode));

        return true;
    }

    internal static List<ContentTypeWriter> DiscoverWriteMethods(Type handlerType)
    {
        var writers = new List<ContentTypeWriter>();

        foreach (var method in handlerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
        {
            if (method.TryGetAttribute<WritesAttribute>(out var attr))
            {
                writers.Add(new ContentTypeWriter(attr!.ContentType, handlerType, method));
            }
            else if (WriteResponseMethodNames.Contains(method.Name))
            {
                // Convention-based, but needs [Writes] to specify content type
                // Without [Writes], skip — we don't know what content type to match
                continue;
            }
        }

        return writers;
    }
}

/// <summary>
/// Represents a discovered WriteResponse method with its content type binding
/// </summary>
internal class ContentTypeWriter
{
    public ContentTypeWriter(string contentType, Type handlerType, MethodInfo method)
    {
        ContentType = contentType;
        HandlerType = handlerType;
        Method = method;
        IsAsync = method.ReturnType == typeof(Task) || method.ReturnType == typeof(ValueTask);
    }

    public string ContentType { get; }
    public Type HandlerType { get; }
    public MethodInfo Method { get; }
    public bool IsAsync { get; }
}

/// <summary>
/// Frame that generates content-negotiated response writing code.
/// Checks the Accept header and dispatches to the appropriate WriteResponse method.
/// </summary>
internal class ContentNegotiationWriteFrame : AsyncFrame
{
    private readonly Variable _resourceVariable;
    private readonly List<ContentTypeWriter> _writers;
    private readonly ConnegMode _mode;
    private Variable? _httpContext;

    public ContentNegotiationWriteFrame(Variable resourceVariable, List<ContentTypeWriter> writers, ConnegMode mode)
    {
        _resourceVariable = resourceVariable;
        _writers = writers;
        _mode = mode;
        uses.Add(resourceVariable);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(Microsoft.AspNetCore.Http.HttpContext));
        yield return _httpContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Content negotiation: selecting response writer based on Accept header");
        writer.Write($"var acceptHeader = {_httpContext!.Usage}.Request.Headers.Accept.ToString();");
        writer.BlankLine();

        var first = true;
        foreach (var contentWriter in _writers)
        {
            var keyword = first ? "if" : "else if";
            first = false;

            writer.Write($"BLOCK:{keyword} (acceptHeader.Contains(\"{contentWriter.ContentType}\"))");

            // Set content type
            writer.Write($"{_httpContext.Usage}.Response.ContentType = \"{contentWriter.ContentType}\";");

            // Call the WriteResponse method
            var call = BuildMethodCall(contentWriter);
            if (contentWriter.IsAsync)
            {
                writer.Write($"await {call};");
            }
            else
            {
                writer.Write($"{call};");
            }

            writer.FinishBlock();
        }

        // Fallback
        if (_mode == ConnegMode.Loose)
        {
            writer.Write("BLOCK:else");
            writer.WriteComment("Fallback to JSON serialization");
            writer.Write($"await {nameof(HttpHandler.WriteJsonAsync)}({_httpContext.Usage}, {_resourceVariable.Usage});");
            writer.FinishBlock();
        }
        else
        {
            writer.Write("BLOCK:else");
            writer.WriteComment("Strict content negotiation: return 406 Not Acceptable");
            writer.Write($"{_httpContext.Usage}.Response.StatusCode = 406;");
            writer.FinishBlock();
        }

        Next?.GenerateCode(method, writer);
    }

    private string BuildMethodCall(ContentTypeWriter contentWriter)
    {
        var typeName = contentWriter.HandlerType.FullNameInCode();
        var methodName = contentWriter.Method.Name;

        // Build parameter list from method signature
        var parameters = contentWriter.Method.GetParameters();
        var args = new List<string>();

        foreach (var param in parameters)
        {
            if (param.ParameterType == typeof(Microsoft.AspNetCore.Http.HttpContext))
            {
                args.Add(_httpContext!.Usage);
            }
            else if (param.ParameterType.IsAssignableFrom(_resourceVariable.VariableType))
            {
                args.Add(_resourceVariable.Usage);
            }
            else
            {
                // For other parameters, use the variable name directly
                args.Add(param.Name!);
            }
        }

        return $"{typeName}.{methodName}({string.Join(", ", args)})";
    }
}
