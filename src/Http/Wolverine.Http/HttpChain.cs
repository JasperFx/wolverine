using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.RuntimeCompiler;
using Lamar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Wolverine.Configuration;
using Wolverine.Http.Metadata;

namespace Wolverine.Http;

public class HttpChain : Chain<HttpChain, Attributes>, ICodeFile
{
    public static readonly Variable[] HttpContextVariables =
        Variable.VariablesForProperties<HttpContext>(HttpGraph.Context);
    
    private readonly HttpGraph _parent;

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
    
    private readonly string _fileName;
    
    // Make the assumption that the route argument has to match the parameter name
    private readonly List<ParameterInfo> _routeArguments = new();
    private GeneratedType _generatedType;
    private Type? _handlerType;
    private readonly List<string> _httpMethods = new();

    public bool HasResourceType()
    {
        return ResourceType != null && ResourceType != typeof(void);
    }

    public MethodCall Method { get; }

    public HttpChain(MethodCall method, HttpGraph parent)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Method = method ?? throw new ArgumentNullException(nameof(method));

        DisplayName = Method.ToString();
        
        // TODO -- need a helper for this in JasperFx.Core
        var att = method.Method.GetAttribute<WolverineHttpMethodAttribute>();
        if (att != null)
        {
            RoutePattern = RoutePatternFactory.Parse(att.Template);

            _httpMethods.Add(att.HttpMethod);
            Order = att.Order;
            DisplayName = att.Name ?? Method.ToString();
        }

        ResourceType = method.ReturnType;

        _fileName = _httpMethods.Select(x => x.ToUpper()).Join("_") + RoutePattern.RawText.Replace("/", "_").Replace("{", "").Replace("}", "").Replace("-", "_");

        Description = _fileName;
        
        _parent.ApplyParameterMatching(this);
        
        // Apply attributes and the Configure() method if that exists too
        applyAttributesAndConfigureMethods(_parent.Rules, _parent.Container);

        // Add Before/After methods from the current handler
        applyImpliedMiddlewareFromHandlers(_parent.Rules);
    }

    /// <summary>
    /// Additional ASP.Net Core metadata for the endpoint
    /// </summary>
    public List<object> Metadata { get; } = new();

    private IEnumerable<object> buildMetadata()
    {
        // For diagnostics
        yield return this;

        yield return Method.Method;
        
        // This is just to let the world know that the endpoint came from Wolverine
        yield return new WolverineMarker();
        
        // Custom metadata
        foreach (var metadata in Metadata)
        {
            yield return metadata;
        }
        
        // TODO -- figure out how to get at the Cors preflight stuff
        yield return new HttpMethodMetadata(_httpMethods);
        
        if (RequestType != null)
        {
            yield return new WolverineAcceptsMetadata(this);
            yield return new WolverineProducesResponse { StatusCode = 400 };
        }

        if (ResourceType != null)
        {
            yield return new WolverineProducesResponse
            {
                StatusCode = 200, 
                Type = ResourceType, 
                ContentTypes = new []{ "application/json"  }
            };
            
            yield return new WolverineProducesResponse
            {
                StatusCode = 404
            };
        }
        else
        {
            yield return new WolverineProducesResponse { StatusCode = 200 };
        }

        foreach (var attribute in Method.HandlerType.GetCustomAttributes())
        {
            yield return attribute;
        }

        foreach (var attribute in Method.Method.GetCustomAttributes())
        {
            yield return attribute;
        }
    }

    public string? DisplayName { get; set; }

    public int Order { get; set; }

    public IEnumerable<string> HttpMethods => _httpMethods;

    public Type? ResourceType { get; }
    
    public RoutePattern RoutePattern { get; }
    
    public Type? RequestType { get; internal set; }

    public override string Description { get; }
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
    
    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        assembly.UsingNamespaces.Fill(typeof(RoutingHttpContextExtensions).Namespace);
        assembly.UsingNamespaces.Fill("System.Linq");
        assembly.UsingNamespaces.Fill("System");
        
        _generatedType = assembly.AddType(_fileName, typeof(HttpHandler));

        assembly.ReferenceAssembly(Method.HandlerType.Assembly);
        assembly.ReferenceAssembly(typeof(HttpContext).Assembly);
        assembly.ReferenceAssembly(typeof(HttpChain).Assembly);
        
        var handleMethod = _generatedType.MethodFor(nameof(HttpHandler.Handle));
        
        handleMethod.DerivedVariables.AddRange(HttpContextVariables);

        handleMethod.Frames.AddRange(DetermineFrames(assembly.Rules));
    }

    internal IEnumerable<Frame> DetermineFrames(GenerationRules rules)
    {
        // Add frames for any writers
        if (ResourceType != typeof(void))
        {
            foreach (var writerPolicy in _parent.WriterPolicies)
            {
                if (writerPolicy.TryApply(this))
                {
                    break;
                }
            }
        }

        foreach (var frame in Middleware)
        {
            yield return frame;
        }

        yield return Method;

        foreach (var frame in Postprocessors)
        {
            yield return frame;
        }

    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider services, string containingNamespace)
    {
        var found = this.As<ICodeFile>().AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider services,
        string containingNamespace)
    {
        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == _fileName);

        if (_handlerType == null)
        {
            return false;
        }

        return true;
    }

    string ICodeFile.FileName => _fileName;
    
    public RouteEndpoint BuildEndpoint()
    {
        var handler = new Lazy<HttpHandler>(() =>
        {
            this.InitializeSynchronously(_parent.Rules, _parent, _parent.Container);
            return (HttpHandler)_parent.Container.QuickBuild(_handlerType);
        });
        
        Endpoint = new RouteEndpoint(c => handler.Value.Handle(c), RoutePattern, Order, new EndpointMetadataCollection(buildMetadata()), DisplayName);

        return Endpoint;
    }
    
    internal RouteEndpoint? Endpoint { get; private set; }
    public string SourceCode => _generatedType.SourceCode;

    public override string ToString()
    {
        return _fileName;
    }

    public override bool RequiresOutbox()
    {
        return ServiceDependencies(_parent.Container).Contains(typeof(IMessageBus)) ;
    }
}