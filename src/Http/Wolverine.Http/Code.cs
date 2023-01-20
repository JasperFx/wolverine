using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Wolverine.Configuration;

namespace Wolverine.Http;

public interface IEndpointSource
{
    public void Apply(WebApplication builder);
}

public class FakeEndpointSource : IEndpointSource
{
    public void Apply(WebApplication builder)
    {

        builder.MapPost("foo", c => Task.CompletedTask);

    }
}

public class EndpointChain : Chain<EndpointChain, ModifyEndpointAttribute>, ICodeFile
{
    public static EndpointChain ChainFor<T>(Expression<Action<T>> expression)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method);

        return new EndpointChain(call);
    }
    
    private string _fileName;
    
    // Make the assumption that the route argument has to match the parameter name
    private readonly List<ParameterInfo> _routeArguments = new();
    
    public MethodCall Method { get; }

    public EndpointChain(MethodCall method)
    {
        Method = method;
        
        // TODO -- need a helper for this in JasperFx.Core
        var att = method.Method.GetAttribute<HttpMethodAttribute>();
        if (att != null)
        {
            RoutePattern = RoutePatternFactory.Parse(att.Template);
            
        }

        ResourceType = method.ReturnType;

        // TODO -- can use RoutePattern to match arguments
        // left over, "primitive" arguments would be query string args. Must be nullable

        // TODO -- See RouteEndpointDataSource for "how" to build metadata

        // RoutePatternFactory
    }
    
    public Type ResourceType { get; private set; }
    
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
        throw new NotImplementedException();
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider services, string containingNamespace)
    {
        throw new NotImplementedException();
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider services,
        string containingNamespace)
    {
        throw new NotImplementedException();
    }

    string ICodeFile.FileName => _fileName;
}