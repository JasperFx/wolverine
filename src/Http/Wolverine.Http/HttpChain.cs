using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FSharp.Core;
using Wolverine.Configuration;
using Wolverine.Http.CodeGen;
using Wolverine.Http.Metadata;
using Wolverine.Http.Policies;
using Wolverine.Persistence;
using Wolverine.Runtime;
using ServiceContainer = JasperFx.ServiceContainer;

namespace Wolverine.Http;

public partial class HttpChain : Chain<HttpChain, ModifyHttpChainAttribute>, ICodeFile, IEndpointNameMetadata, IEndpointSummaryMetadata, IEndpointDescriptionMetadata, IDescribeMyself
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

    internal Variable? RequestBodyVariable { get; set; }

    private string? _fileName;
    private readonly List<string> _httpMethods = [];

    private readonly List<Variable> _routeVariables = [];

    private readonly HttpGraph _parent;

    private readonly List<HttpElementVariable> _querystringVariables = [];

    private readonly List<HttpElementVariable> _formValueVariables = [];

    public string OperationId { get; set; }
    
    /// <summary>
    /// This may be overridden by some IResponseAware policies in place of the first
    /// create variable of the method call
    /// </summary>
    [IgnoreDescription]
    public Variable? ResourceVariable { get; set; }

    // Make the assumption that the route argument has to match the parameter name
    private GeneratedType? _generatedType;
    private Type? _handlerType;
    private string _description;
    private Type? _requestType;

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

            if (att.RouteName.IsNotEmpty())
            {
                RouteName = att.RouteName;
            }

            if (att.OperationId.IsNotEmpty())
            {
                OperationId = att.OperationId;
            }
        }

        OperationId ??= $"{Method.HandlerType.FullNameInCode()}.{Method.Method.Name}";

        // Apply attributes and the Configure() method if that exists too
        applyAttributesAndConfigureMethods(_parent.Rules, _parent.Container);

        // Add Before/After methods from the current handler
        ApplyImpliedMiddlewareFromHandlers(_parent.Rules);

        foreach (var call in Middleware.OfType<MethodCall>().ToArray())
        {
            parent.ApplyParameterMatching(this, call);
        }

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

    [IgnoreDescription]
    public MethodCall Method { get; }

    public Type EndpointType => Method.HandlerType;

    public string? RouteName { get; set; }

    [IgnoreDescription]
    public string? DisplayName { get; set; }
    
    public int Order { get; set; }

    [IgnoreDescription]
    public IEnumerable<string> HttpMethods => _httpMethods;

    public Type? ResourceType { get; private set; }

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

        // Doing this prevents middleware policies
        // from doing something stupid
        RequestType ??= typeof(void);
    }

    [IgnoreDescription]
    public RoutePattern? RoutePattern { get; private set; }

    public Type? RequestType
    {
        get => _requestType;
        internal set
        {
            _requestType = value;
            if (_requestType != null)
            {
                applyAuditAttributes(_requestType);
            }
        }
    }

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

        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();

        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);

        var serviceContainer = registry.BuildServiceProvider();
        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), serviceContainer.GetRequiredService<IServiceContainer>()));
    }

    public static HttpChain ChainFor(Type handlerType, string methodName, HttpGraph? parent = null)
    {
        var call = new MethodCall(handlerType, methodName);

        var registry = new ServiceCollection();
        registry.AddSingleton<JsonSerializerOptions>();
        registry.AddTransient<IServiceVariableSource, ServiceCollectionServerVariableSource>();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);

        var provider = registry.BuildServiceProvider();
        
        var serviceContainer = provider.GetRequiredService<IServiceContainer>();
        
        return new HttpChain(call, parent ?? new HttpGraph(new WolverineOptions(), serviceContainer));
    }

    public bool HasResourceType()
    {
        return ResourceType != null && ResourceType != typeof(void) && ResourceType != typeof(Unit);
    }

    public override bool ShouldFlushOutgoingMessages()
    {
        return true;
    }

    public override MethodCall[] HandlerCalls()
    {
        return [Method];
    }

    public override bool HasAttribute<T>()
    {
        return Method.HandlerType.HasAttribute<T>() || Method.Method.HasAttribute<T>();
    }

    public override Type? InputType()
    {
        return HasRequestType ? RequestType : ComplexQueryStringType;
    }

    public override Frame[] AddStopConditionIfNull(Variable variable)
    {
        return [new SetStatusCodeAndReturnIfEntityIsNullFrame(variable)];
    }

    public override Frame[] AddStopConditionIfNull(Variable data, Variable? identity, IDataRequirement requirement)
    {
        var message = requirement.MissingMessage ?? $"Unknown {data.VariableType.NameInCode()} with identity {{Id}}";
        
        // TODO -- want to use WolverineOptions here for a default
        switch (requirement.OnMissing)
        {
            case OnMissing.Simple404:
                Metadata.Produces(404);
                return [new SetStatusCodeAndReturnIfEntityIsNullFrame(data)];
                
            case OnMissing.ProblemDetailsWith400:
                Metadata.Produces(400, contentType: "application/problem+json");
                return [new WriteProblemDetailsIfNull(data, identity, message, 400)];
            case OnMissing.ProblemDetailsWith404:
                Metadata.Produces(404, contentType: "application/problem+json");
                return [new WriteProblemDetailsIfNull(data, identity, message, 404)];
                
            default:
                return [new ThrowRequiredDataMissingExceptionFrame(data, identity, message)];
        }
    }

    public override string ToString()
    {
        return _fileName!;
    }

    public OptionsDescription ToDescription()
    {
        var description = new OptionsDescription(this);
        description.AddValue(nameof(HttpMethods), HttpMethods.ToArray());

        description.AddValue("Route", RoutePattern.RawText);

        if (Tags.Any())
        {
            description.AddValue("Tags", Tags.Select(pair => $"{pair.Key} = {pair.Value}").Join(", "));
        }

        description.AddValue("Endpoint", $"{Method.HandlerType.FullNameInCode()}.{Method.MethodSignature}");

        return description;
    }

    public override bool RequiresOutbox()
    {
        return ServiceDependencies(_parent.Container, Type.EmptyTypes).Contains(typeof(IMessageBus)) || ServiceDependencies(_parent.Container, Type.EmptyTypes).Contains(typeof(MessageContext));
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
            .WithMetadata(new HttpMethodMetadata(_httpMethods));
            //.WithMetadata(Method.Method);

        if (HasRequestType)
        {
            if(IsFormData){
                Metadata.Accepts(RequestType, true, "application/x-www-form-urlencoded");
            }else{
                Metadata.Accepts(RequestType, false, "application/json");
            }
        }

        foreach (var attribute in Method.HandlerType.GetCustomAttributes()) Metadata.WithMetadata(attribute);
        foreach (var attribute in Method.Method.GetCustomAttributes()) Metadata.WithMetadata(attribute);
    }


    public HttpElementVariable? TryFindOrCreateFormValue(ParameterInfo parameter)
    {
        var parameterName = parameter.Name;
        var key = parameterName;
        var parameterType = parameter.ParameterType;

        if (parameter.TryGetAttribute<FromFormAttribute>(out var att) && att.Name.IsNotEmpty())
        {
            key = att.Name;
        }

        return TryFindOrCreateFormValue(parameterType, parameterName, key);
    }
    
 public HttpElementVariable? TryFindOrCreateFormValue(Type parameterType, string parameterName, string? key = null){
        key ??= parameterName;
        var variable = _formValueVariables.FirstOrDefault(x => x.Name == key);
        if (variable == null)
        {   
            if (parameterType == typeof(string))
            {
                variable = new ReadHttpFrame(BindingSource.Form, parameterType,key).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }
            if (parameterType == typeof(string[]))
            {
                variable = new ParsedArrayFormValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }

            if (parameterType.IsNullable())
            {
                var inner = parameterType.GetInnerTypeFromNullable();
                if (RouteParameterStrategy.CanParse(inner))
                {
                    variable = new ReadHttpFrame(BindingSource.Form, parameterType,key).Variable;
                    variable.Name = key;
                    _formValueVariables.Add(variable);
                }
            }
            
            if (parameterType.IsArray && RouteParameterStrategy.CanParse(parameterType.GetElementType()))
            {
                variable = new ParsedArrayFormValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }

            if (ParsedCollectionQueryStringValue.CanParse(parameterType))
            {
                variable = new ParsedCollectionFormValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }

            if (RouteParameterStrategy.CanParse(parameterType))
            {
                variable = new ReadHttpFrame(BindingSource.Form, parameterType,key).Variable;
                variable.Name = key;
                _formValueVariables.Add(variable);
            }
        }
        else if (variable.VariableType != parameterType)
        {
            throw new InvalidOperationException(
                $"The form value parameter '{key}' cannot be used for multiple target types");
        }

        return variable;
    }
 
    public bool FindQuerystringVariable(Type variableType, string routeOrParameterName, [NotNullWhen(true)]out Variable? variable)
    {
        var matched = Method.Method.GetParameters()
            .FirstOrDefault(x => x.ParameterType == variableType && x.Name != null && x.Name.EqualsIgnoreCase(routeOrParameterName));
        if (matched is not null)
        {
            variable = TryFindOrCreateQuerystringValue(matched);
            if (variable is not null)
            {
                return true;
            }
        }

        variable = null;
        return false;
    }

    public HttpElementVariable? TryFindOrCreateQuerystringValue(ParameterInfo parameter)
    {
        var parameterName = parameter.Name;
        var key = parameterName;
        var parameterType = parameter.ParameterType;

        if (parameter.TryGetAttribute<FromQueryAttribute>(out var att) && att.Name.IsNotEmpty())
        {
            key = att.Name;
        }

        return TryFindOrCreateQuerystringValue(parameterType, parameterName, key);
    }

    public HttpElementVariable? TryFindOrCreateQuerystringValue(Type parameterType, string parameterName, string? key = null)
    {
        key ??= parameterName;
        var variable = _querystringVariables.FirstOrDefault(x => x.Name == key);
        if (variable == null)
        {
            if (parameterType == typeof(string))
            {
                variable = new ReadHttpFrame(BindingSource.QueryString, parameterType, key).Variable;
                variable.Name = key;

                if (variable.Usage == "tenantId")
                {
                    variable.OverrideName("tenantIdString");
                }
                
                _querystringVariables.Add(variable);
            }

            if (parameterType == typeof(string[]))
            {
                variable = new ParsedArrayQueryStringValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _querystringVariables.Add(variable);
            }

            if (parameterType.IsNullable())
            {
                var inner = parameterType.GetInnerTypeFromNullable();
                if (RouteParameterStrategy.CanParse(inner))
                {
                    //variable = new ParsedNullableQueryStringValue(parameterType, parameterName).Variable;
                    variable = new ReadHttpFrame(BindingSource.QueryString, parameterType, parameterName).Variable;
                    variable.Name = key;
                    _querystringVariables.Add(variable);
                }
            }
            
            if (parameterType.IsArray && RouteParameterStrategy.CanParse(parameterType.GetElementType()))
            {
                variable = new ParsedArrayQueryStringValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _querystringVariables.Add(variable);
            }

            if (ParsedCollectionQueryStringValue.CanParse(parameterType))
            {
                variable = new ParsedCollectionQueryStringValue(parameterType, parameterName).Variable;
                variable.Name = key;
                _querystringVariables.Add(variable);
            }

            if (RouteParameterStrategy.CanParse(parameterType))
            {
                //variable = new ParsedQueryStringValue(parameterType, parameterName).Variable;
                variable = new ReadHttpFrame(BindingSource.QueryString, parameterType, parameterName).Variable;
                variable.Name = key;
                _querystringVariables.Add(variable);
            }
        }
        else if (variable.VariableType != parameterType)
        {
            throw new InvalidOperationException(
                $"The query string parameter '{key}' cannot be used for multiple target types");
        }

        return variable;
    }

    public bool FindRouteVariable(ParameterInfo parameter, [NotNullWhen(true)]out Variable? variable)
    {
        var existing = _routeVariables.FirstOrDefault(x =>
            x.VariableType == parameter.ParameterType && x.Usage.EqualsIgnoreCase(parameter.Name));

        if (existing is not null)
        {
            variable = existing;
            return true;
        }

        var matchingRouteParameter = RoutePattern!.Parameters.FirstOrDefault(x => x.Name == parameter.Name);
        if (matchingRouteParameter != null)
        {
            var isOptional = matchingRouteParameter.IsOptional;
            
            if (parameter.ParameterType == typeof(string))
            {
                variable = new ReadHttpFrame(BindingSource.RouteValue, typeof(string), parameter.Name!, isOptional).Variable;
                _routeVariables.Add(variable);
                return true;
            }
            
            if (parameter.ParameterType.IsNullable())
            {
                var inner = parameter.ParameterType.GetInnerTypeFromNullable();
                if (RouteParameterStrategy.CanParse(inner))
                {
                    variable = new ReadHttpFrame(BindingSource.RouteValue, parameter.ParameterType, parameter.Name, isOptional).Variable;
                    _routeVariables.Add(variable);
                    return true;
                }
            }

            if (RouteParameterStrategy.CanParse(parameter.ParameterType))
            {
                variable = new ReadHttpFrame(BindingSource.RouteValue, parameter.ParameterType, parameter.Name, isOptional).Variable;
                _routeVariables.Add(variable);
                return true;
            }
        }

        variable = default;
        return false;
    }

    public bool FindRouteVariable(Type variableType, string routeOrParameterName, [NotNullWhen(true)]out Variable? variable)
    {
        var matched =
            _routeVariables.FirstOrDefault(x => x.VariableType == variableType && x.Usage.EqualsIgnoreCase(routeOrParameterName));
        if (matched is not null)
        {
            variable = matched;
            return true;
        }

        var matches = RoutePattern!.Parameters.Any(x => x.Name.EqualsIgnoreCase(routeOrParameterName));
        if (matches)
        {
            if (variableType == typeof(string) || RouteParameterStrategy.CanParse(variableType))
            {
                var frame = new ReadHttpFrame(BindingSource.RouteValue, variableType, routeOrParameterName)
                {
                    Key = routeOrParameterName
                };
                
                variable = frame.Variable;
                _routeVariables.Add(variable);
                return true;
            }
        }

        variable = default;
        return false;

    }

    private readonly List<HttpElementVariable> _headerVariables = [];

    public HttpElementVariable GetOrCreateHeaderVariable(IFromHeaderMetadata metadata, ParameterInfo parameter)
    {
        var existing =
            _headerVariables.FirstOrDefault(x => x.Name == metadata.Name && x.VariableType == parameter.ParameterType);

        if (existing != null) return existing;

        var frame = new ReadHttpFrame(BindingSource.Header, parameter.ParameterType, parameter.Name)
        {
            Key = metadata.Name ?? parameter.Name
        };
        
        _headerVariables.Add(frame.Variable);
        
        return frame.Variable;
    }
    
    public HttpElementVariable GetOrCreateHeaderVariable(IFromHeaderMetadata metadata, PropertyInfo property)
    {
        var existing =
            _headerVariables.FirstOrDefault(x => x.Name == metadata.Name && x.VariableType == property.PropertyType);

        if (existing != null) return existing;

        var frame = new ReadHttpFrame(BindingSource.Header, property.PropertyType, property.Name)
        {
            Key = metadata.Name ?? property.Name
        };
        
        _headerVariables.Add(frame.Variable);
        
        return frame.Variable;
    }

    string IEndpointNameMetadata.EndpointName => ToString();

    string IEndpointSummaryMetadata.Summary => ToString();

    public List<ParameterInfo> FileParameters { get; } = [];

    [MemberNotNullWhen(true, nameof(RequestType))]
    public bool HasRequestType => RequestType != null && RequestType != typeof(void);

    public bool IsFormData { get; internal set; }
    public Type? ComplexQueryStringType { get; set; }
    public ServiceProviderSource ServiceProviderSource { get; set; } = ServiceProviderSource.IsolatedAndScoped;

    internal Variable BuildJsonDeserializationVariable()
    {
        return _parent.BuildJsonDeserializationVariable(this);
    }

    public override void ApplyParameterMatching(MethodCall call)
    {
        _parent.ApplyParameterMatching(this, call);
    }

    public bool TryReplaceServiceProvider(out Variable serviceProvider)
    {
        serviceProvider = default!;
        if (ServiceProviderSource == ServiceProviderSource.IsolatedAndScoped) return false;

        serviceProvider = new Variable(typeof(IServiceProvider), $"httpContext.{nameof(HttpContext.RequestServices)}");
        return true;
    }
}

