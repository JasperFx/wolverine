using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Wolverine.Configuration;

namespace Wolverine.Http;

public partial class HttpChain : Chain<HttpChain, Attributes>, ICodeFile
{
    public static readonly Variable[] HttpContextVariables =
        Variable.VariablesForProperties<HttpContext>(HttpGraph.Context);

    private readonly string _fileName;
    private readonly List<string> _httpMethods = new();

    private readonly HttpGraph _parent;

    // Make the assumption that the route argument has to match the parameter name
    private readonly List<ParameterInfo> _routeArguments = new();
    private GeneratedType _generatedType;
    private Type? _handlerType;

    public HttpChain(MethodCall method, HttpGraph parent)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Method = method ?? throw new ArgumentNullException(nameof(method));

        DisplayName = Method.ToString();

        if (method.Method.TryGetAttribute<WolverineHttpMethodAttribute>(out var att))
        {
            RoutePattern = RoutePatternFactory.Parse(att.Template);

            _httpMethods.Add(att.HttpMethod);
            Order = att.Order;
            DisplayName = att.Name ?? Method.ToString();
        }

        ResourceType = method.Creates.FirstOrDefault()?.VariableType;

        _fileName = _httpMethods.Select(x => x.ToUpper()).Join("_") + RoutePattern.RawText.Replace("/", "_")
            .Replace("{", "").Replace("}", "").Replace("-", "_");

        Description = _fileName;

        _parent.ApplyParameterMatching(this);

        // Apply attributes and the Configure() method if that exists too
        applyAttributesAndConfigureMethods(_parent.Rules, _parent.Container);

        // Add Before/After methods from the current handler
        applyImpliedMiddlewareFromHandlers(_parent.Rules);

        Metadata = new RouteHandlerBuilder(new[] { this });
        applyMetadata();
    }

    public MethodCall Method { get; }
    
    public string? DisplayName { get; set; }

    public int Order { get; set; }

    public IEnumerable<string> HttpMethods => _httpMethods;

    public Type? ResourceType { get; }

    public RoutePattern RoutePattern { get; }

    public Type? RequestType { get; internal set; }

    public override string Description { get; }

    internal RouteEndpoint? Endpoint { get; private set; }


    public static HttpChain ChainFor<T>(Expression<Action<T>> expression, HttpGraph? parent = null)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method);

        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        })));
    }

    public static HttpChain ChainFor(Type handlerType, string methodName, HttpGraph? parent = null)
    {
        var call = new MethodCall(handlerType, methodName);

        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        })));
    }

    public bool HasResourceType()
    {
        return ResourceType != null && ResourceType != typeof(void);
    }

    public override bool ShouldFlushOutgoingMessages()
    {
        return true;
    }

    public override MethodCall[] HandlerCalls()
    {
        return new[] { Method };
    }

    public override bool HasAttribute<T>()
    {
        return Method.HandlerType.HasAttribute<T>() || Method.Method.HasAttribute<T>();
    }

    public override Type? InputType()
    {
        return RequestType;
    }

    public override string ToString()
    {
        return _fileName;
    }

    public override bool RequiresOutbox()
    {
        return ServiceDependencies(_parent.Container).Contains(typeof(IMessageBus));
    }

    private void applyMetadata()
    {
        Metadata
            .WithMetadata(this)
            .WithMetadata(new WolverineMarker())
            .WithMetadata(new HttpMethodMetadata(_httpMethods))
            .WithMetadata(Method.Method);

        if (RequestType != null)
        {
            Metadata.Accepts(RequestType, false, "application/json");
            Metadata.Produces(400);
        }

        if (ResourceType != null)
        {
            Metadata.Produces(200, ResourceType, "application/json");
            Metadata.Produces(404);
        }
        else
        {
            Metadata.Produces(200);
        }

        foreach (var attribute in Method.HandlerType.GetCustomAttributes()) Metadata.WithMetadata(attribute);

        foreach (var attribute in Method.Method.GetCustomAttributes()) Metadata.WithMetadata(attribute);
    }
}