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

public class EndpointChain : Chain<EndpointChain, ModifyEndpointAttribute>, ICodeFile
{
    public static readonly Variable[] HttpContextVariables =
        Variable.VariablesForProperties<HttpContext>(EndpointGraph.Context);
    
    private readonly EndpointGraph _parent;

    public static EndpointChain ChainFor<T>(Expression<Action<T>> expression, EndpointGraph? parent = null)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method);

        return new EndpointChain(call, parent ?? new EndpointGraph(new WolverineOptions(), new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        })));
    }
    
    public static EndpointChain ChainFor(Type handlerType, string methodName, EndpointGraph? parent = null)
    {
        var call = new MethodCall(handlerType, methodName);

        return new EndpointChain(call, parent ?? new EndpointGraph(new WolverineOptions(), new Container(x =>
        {
            x.For<JsonSerializerOptions>().Use(new JsonSerializerOptions());
            x.For<IServiceVariableSource>().Use(c => c.CreateServiceVariableSource()).Singleton();
        })));
    }
    
    private string _fileName;
    
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

    public EndpointChain(MethodCall method, EndpointGraph parent)
    {
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Method = method ?? throw new ArgumentNullException(nameof(method));

        DisplayName = Method.ToString();
        
        // TODO -- need a helper for this in JasperFx.Core
        var att = method.Method.GetAttribute<HttpMethodAttribute>();
        if (att != null)
        {
            RoutePattern = RoutePatternFactory.Parse(att.Template);

            _httpMethods.AddRange(att.HttpMethods);
            Order = att.Order;
            DisplayName = att.Name ?? Method.ToString();
        }

        ResourceType = method.ReturnType;

        _fileName = _httpMethods.Select(x => x.ToUpper()).Join("_") + RoutePattern.RawText.Replace("/", "_").Replace("{", "").Replace("}", "");

        Description = _fileName;
        
        _parent.ApplyParameterMatching(this);

    }

    // TODO -- will need to be able to add other metadata later. Policies
    // will need to be able to edit this. Example is adding ProblemDetails
    private IEnumerable<object> buildMetadata()
    {
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

    public Type ResourceType { get; }
    
    public RoutePattern RoutePattern { get; }
    
    public Type RequestType { get; internal set; }

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

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        assembly.UsingNamespaces.Fill(typeof(RoutingHttpContextExtensions).Namespace);
        assembly.UsingNamespaces.Fill("System.Linq");
        assembly.UsingNamespaces.Fill("System");
        
        _generatedType = assembly.AddType(_fileName, typeof(EndpointHandler));

        assembly.ReferenceAssembly(Method.HandlerType.Assembly);
        assembly.ReferenceAssembly(typeof(HttpContext).Assembly);
        assembly.ReferenceAssembly(typeof(EndpointChain).Assembly);
        
        var handleMethod = _generatedType.MethodFor(nameof(EndpointHandler.Handle));
        //handleMethod.AsyncMode = AsyncMode.AsyncTask; // Might not be necessary anymore
        
        handleMethod.DerivedVariables.AddRange(HttpContextVariables);

        handleMethod.Frames.AddRange(DetermineFrames(assembly.Rules));
    }

    internal IEnumerable<Frame> DetermineFrames(GenerationRules rules)
    {
        // TODO -- apply customizations from attributes if any
        
        // Add frames for any writers
        if (ResourceType != typeof(void))
        {
            foreach (var writerPolicy in _parent.WriterPolicies)
            {
                if (writerPolicy.TryApply(this)) break;
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
        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == _generatedType.TypeName);

        if (_handlerType == null)
        {
            return false;
        }

        return true;
    }

    string ICodeFile.FileName => _fileName;
    
    public RouteEndpoint BuildEndpoint()
    {
        var handler = new Lazy<EndpointHandler>(() =>
        {
            this.InitializeSynchronously(_parent.Rules, _parent, _parent.Container);
            return (EndpointHandler)_parent.Container.QuickBuild(_handlerType);
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