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
using Wolverine.Http.CodeGen;
using Wolverine.Http.Metadata;

namespace Wolverine.Http;

public partial class HttpChain : Chain<HttpChain, ModifyHttpChainAttribute>, ICodeFile
{
    public static bool IsValidResponseType(Type type)
    {
        if (type == typeof(IEnumerable<object>) || type == typeof(object[]))
        {
            return false;
        }

        if (type.CanBeCastTo<IWolverineReturnType>())
        {
            return false;
        }

        if (type.CanBeCastTo<IAsyncEnumerable<object>>())
        {
            return false;
        }

        return true;
    }

    public static readonly Variable[] HttpContextVariables =
        Variable.VariablesForProperties<HttpContext>(HttpGraph.Context);

    private string? _fileName;
    private readonly List<string> _httpMethods = new();

    private readonly List<Variable> _routeVariables = new();

    private readonly HttpGraph _parent;

    // Make the assumption that the route argument has to match the parameter name
    private GeneratedType? _generatedType;
    private Type? _handlerType;
    private string _description;
    
    public HttpChain(MethodCall method, HttpGraph parent)
    {
        _description = method.ToString();
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Method.CommentText = "The actual HTTP request handler execution";

        DisplayName = Method.ToString();

        if (tryFindResourceType(method, out var responseType))
        {
            NoContent = false;
            ResourceType = responseType;
        }
        else
        {
            NoContent = true;
            ResourceType = typeof(void);
        }
        
        Metadata = new RouteHandlerBuilder(new[] { this });
        
        if (method.Method.TryGetAttribute<WolverineHttpMethodAttribute>(out var att))
        {
            MapToRoute(att.HttpMethod, att.Template, att.Order);
            if (att.Name.IsNotEmpty())
            {
                DisplayName = att.Name;
            }
        }

        // Apply attributes and the Configure() method if that exists too
        applyAttributesAndConfigureMethods(_parent.Rules, _parent.Container);

        // Add Before/After methods from the current handler
        applyImpliedMiddlewareFromHandlers(_parent.Rules);

        applyMetadata();
    }

    private bool tryFindResourceType(MethodCall method, out Type resourceType)
    {
        resourceType = typeof(void);

        if (!method.Creates.Any())
        {
            return false;
        }

        if (method.Method.HasAttribute<EmptyResponseAttribute>() ||
            method.HandlerType.HasAttribute<EmptyResponseAttribute>())
        {
            return false;
        }


        var responseBody = method.Creates.First();

        resourceType = responseBody.VariableType;
        return IsValidResponseType(resourceType);
    }

    public bool NoContent { get; }

    public MethodCall Method { get; }

    public string? DisplayName { get; set; }

    public int Order { get; set; }

    public IEnumerable<string> HttpMethods => _httpMethods;

    public Type? ResourceType { get; }

    internal void MapToRoute(string method, string url, int? order = null, string? displayName = null)
    {
        RoutePattern = RoutePatternFactory.Parse(url);
        _httpMethods.Fill(method);
        if (order != null)
        {
            Order = order.Value;
        }

        if (displayName.IsNotEmpty())
        {
            DisplayName = displayName;
        }

        _fileName = determineFileName();

        _description = _fileName;

        _parent.ApplyParameterMatching(this);
    }

    public RoutePattern? RoutePattern { get; private set; }

    public Type? RequestType { get; internal set; }

    public override string Description => _description;

    internal RouteEndpoint? Endpoint { get; private set; }
    
    /// <summary>
    /// Required TenancyMode for this http chain
    /// </summary>
    public TenancyMode? TenancyMode { get; set; }
    

    public static HttpChain ChainFor<T>(Expression<Action<T>> expression, HttpGraph? parent = null)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method!);

        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Transient();
        })));
    }

    public static HttpChain ChainFor(Type handlerType, string methodName, HttpGraph? parent = null)
    {
        var call = new MethodCall(handlerType, methodName);

        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Transient();
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
        return _fileName!;
    }

    public override bool RequiresOutbox()
    {
        return ServiceDependencies(_parent.Container, Type.EmptyTypes).Contains(typeof(IMessageBus));
    }

    private void applyMetadata()
    {
        if (RoutePattern != null)
        {
            foreach (var parameter in RoutePattern.Parameters)
            {
                Metadata.WithMetadata(new FromRouteMetadata(parameter.Name));
            }
        }
        
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

    public bool FindRouteVariable(ParameterInfo parameter, out Variable variable)
    {
        var existing = _routeVariables.FirstOrDefault(x =>
            x.VariableType == parameter.ParameterType && x.Usage.EqualsIgnoreCase(parameter.Name));

        if (existing is not null)
        {
            variable = existing;
            return true;
        }
        
        var matches = RoutePattern!.Parameters.Any(x => x.Name == parameter.Name);
        if (matches)
        {
            if (parameter.ParameterType == typeof(string))
            {
                variable = new ReadStringRouteValue(parameter.Name!).Variable;
                _routeVariables.Add(variable);
                return true;
            }

            if (RouteParameterStrategy.CanParse(parameter.ParameterType))
            {
                variable = new ParsedRouteArgumentFrame(parameter).Variable;
                _routeVariables.Add(variable);
                return true;
            }
        }

        variable = default!;
        return false;
    }

    public bool FindRouteVariable(Type variableType, string routeOrParameterName, out Variable variable)
    {
        var matched =
            _routeVariables.FirstOrDefault(x => x.VariableType == variableType && x.Usage == routeOrParameterName);
        if (matched is not null)
        {
            variable = matched;
            return true;
        }
        
        var matches = RoutePattern!.Parameters.Any(x => x.Name == routeOrParameterName);
        if (matches)
        {
            if (variableType == typeof(string))
            {
                variable = new ReadStringRouteValue(routeOrParameterName!).Variable;
                _routeVariables.Add(variable);
                return true;
            }

            if (RouteParameterStrategy.CanParse(variableType))
            {
                variable = new ParsedRouteArgumentFrame(variableType, routeOrParameterName).Variable;
                _routeVariables.Add(variable);
                return true;
            }
        }

        variable = default!;
        return false;

    }
}