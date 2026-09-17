using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Wolverine.Http.CodeGen;

internal class RequestServicesVariableSource : IVariableSource
{
    private readonly Type _serviceType;
    private RequestServicesFrame? _frame;

    public RequestServicesVariableSource(Type serviceType)
    {
        _serviceType = serviceType;
    }

    public bool Matches(Type type)
    {
        return type == _serviceType;
    }

    public Variable Create(Type type)
    {
        // One frame per chain. A fresh frame per call declared the local twice when two methods took the type.
        _frame ??= new RequestServicesFrame(type);
        return _frame.Service;
    }
}

internal class RequestServicesFrame : SyncFrame
{
    public RequestServicesFrame(Type serviceType) 
    {
        Service = new Variable(serviceType, this);
    }

    public Variable Service { get; }
    
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("This app is configured to source this service from the HttpContext.RequestServices");
        writer.WriteLine($"var {Service.Usage} = {typeof(ServiceProviderServiceExtensions).FullNameInCode()}.{nameof(ServiceProviderServiceExtensions.GetRequiredService)}<{Service.VariableType.FullNameInCode()}>(httpContext.{nameof(HttpContext.RequestServices)});");
        
        Next?.GenerateCode(method, writer);
    }
}