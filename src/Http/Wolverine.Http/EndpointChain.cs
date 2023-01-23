using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Wolverine.Configuration;

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

        return new EndpointChain(call, parent ?? new EndpointGraph());
    }
    
    private string _fileName;
    
    // Make the assumption that the route argument has to match the parameter name
    private readonly List<ParameterInfo> _routeArguments = new();
    private GeneratedType _generatedType;
    private Type? _handlerType;

    public MethodCall Method { get; }

    public EndpointChain(MethodCall method, EndpointGraph parent)
    {
        _parent = parent;
        Method = method;
        
        // TODO -- need a helper for this in JasperFx.Core
        var att = method.Method.GetAttribute<HttpMethodAttribute>();
        if (att != null)
        {
            RoutePattern = RoutePatternFactory.Parse(att.Template);
            
        }

        ResourceType = method.ReturnType;

        // TODO -- prefix with HTTP verb
        _fileName = RoutePattern.RawText.Replace("/", "").Replace("{", "").Replace("}", "");

        // TODO -- can use RoutePattern to match arguments
        // left over, "primitive" arguments would be query string args. Must be nullable

        // TODO -- See RouteEndpointDataSource for "how" to build metadata

        // RoutePatternFactory
    }

    public Type ResourceType { get; }
    
    public RoutePattern RoutePattern { get; }

    public override string Description { get; }
    public override bool ShouldFlushOutgoingMessages()
    {
        return true;
    }

    public override MethodCall[] HandlerCalls()
    {
        return new[] { Method };
    }

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
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
        // TODO -- have to take in IContainer later
        
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
        
        // TODO -- create the actual handler

        return true;
    }

    string ICodeFile.FileName => _fileName;
}