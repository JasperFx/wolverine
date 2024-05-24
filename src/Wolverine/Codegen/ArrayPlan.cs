using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Codegen;

internal class ArrayFamily : ServiceFamily
{
    public ArrayFamily(Type serviceType) : base(serviceType, [new ServiceDescriptor(serviceType, serviceType, ServiceLifetime.Scoped)])
    {
        ElementType = serviceType.GetElementType() ?? serviceType.GenericTypeArguments[0];
    }

    public Type ElementType { get; }

    public override ServicePlan? BuildDefaultPlan(ServiceContainer graph, List<ServiceDescriptor> trail)
    {
        var plans = graph.FindAll(ElementType, trail);
        if (plans.All(x => x.Lifetime == ServiceLifetime.Singleton))
        {
            return new SingletonPlan(Default);
        }

        if (plans.All(x => x.Lifetime != ServiceLifetime.Singleton))
        {
            return new ArrayPlan(ElementType, plans, Default);
        }

        // If it's mixed, we have to do this:(
        return new ServiceLocationPlan(Default);
    }
}

internal class ArrayPlan : ServicePlan
{
    private readonly IReadOnlyList<ServicePlan> _elements;
    private readonly Type? _elementType;

    public ArrayPlan(Type elementType, IReadOnlyList<ServicePlan> elements, ServiceDescriptor @default) : base(@default)
    {
        _elements = elements;
        _elementType = elementType;
    }

    protected override bool requiresServiceProvider(IMethodVariables method)
    {
        return _elements.Any(x => x.RequiresServiceProvider(method));
    }

    public override string WhyRequireServiceProvider(IMethodVariables method)
    {
        var text = "";
        foreach (var dependency in _elements)
        {
            if (dependency.RequiresServiceProvider(method))
            {
                text += Environment.NewLine;
                text += "Dependency: " + dependency + Environment.NewLine;
                text += dependency.WhyRequireServiceProvider(method);
                text += Environment.NewLine;
            }
        }
        
        return text;
    }

    public override Variable CreateVariable(ServiceVariables resolverVariables)
    {
        var elements = _elements.Select(resolverVariables.Resolve).ToArray();
        return new CreateArrayFrame(ServiceType, _elementType, elements).Variable;
    }
}

public class CreateArrayFrame : SyncFrame
{
    private readonly Type _serviceType;
    private readonly Type _elementType;
    private readonly Variable[] _elements;

    public CreateArrayFrame(Type serviceType, Type elementType, Variable[] elements)
    {
        _serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));
        _elementType = elementType ?? throw new ArgumentNullException(nameof(elementType));
        _elements = elements;
        Variable = new Variable(serviceType, this);

        uses.AddRange(elements);
    }
    
    public Variable Variable { get; }
    
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"{_serviceType.FullNameInCode()} {Variable.Usage} = new {_elementType.FullNameInCode()}[]{{{_elements.Select(x => x.Usage).Join(", ")}}};");
        Next?.GenerateCode(method, writer);
    }
}