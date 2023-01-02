using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Attributes;

namespace Wolverine.Configuration;

public interface IModifyChain<T> where T : IChain
{
    void Modify(T chain, GenerationRules rules);
}

public abstract class Chain<TChain, TModifyAttribute> : IChain
    where TChain : Chain<TChain, TModifyAttribute>
    where TModifyAttribute : Attribute, IModifyChain<TChain>
{
    public IList<Frame> Middleware { get; } = new List<Frame>();

    public IList<Frame> Postprocessors { get; } = new List<Frame>();
    public abstract string Description { get; }
    public abstract bool ShouldFlushOutgoingMessages();

    public abstract MethodCall[] HandlerCalls();

    /// <summary>
    ///     Find all of the service dependencies of the current chain
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    public IEnumerable<Type> ServiceDependencies(IContainer container)
    {
        return serviceDependencies(container).Distinct();
    }

    private bool isConfigureMethod(MethodInfo method)
    {
        if (method.Name != "Configure")
        {
            return false;
        }

        if (method.GetParameters().Length != 1)
        {
            return false;
        }

        if (typeof(TChain).CanBeCastTo(method.GetParameters().Single().ParameterType))
        {
            return true;
        }

        return false;
    }


    protected void applyAttributesAndConfigureMethods(GenerationRules rules, IContainer container)
    {
        var handlers = HandlerCalls();
        var configureMethods = handlers.Select(x => x.HandlerType).Distinct()
            .SelectMany(x => x.GetTypeInfo().GetMethods())
            .Where(isConfigureMethod);

        foreach (var method in configureMethods) method?.Invoke(null, new object[] { this });

        var handlerAtts = handlers.SelectMany(x => x.HandlerType.GetTypeInfo()
            .GetCustomAttributes<TModifyAttribute>());

        var methodAtts = handlers.SelectMany(x => x.Method.GetCustomAttributes<TModifyAttribute>());

        foreach (var attribute in handlerAtts.Concat(methodAtts)) attribute.Modify(this.As<TChain>(), rules);

        var genericHandlerAtts = handlers.SelectMany(x => x.HandlerType.GetTypeInfo()
            .GetCustomAttributes<ModifyChainAttribute>());

        var genericMethodAtts = handlers.SelectMany(x => x.Method.GetCustomAttributes<ModifyChainAttribute>());

        foreach (var attribute in genericHandlerAtts.Concat(genericMethodAtts))
            attribute.Modify(this, rules, container);
    }

    private IEnumerable<Type> serviceDependencies(IContainer container)
    {
        foreach (var handlerCall in HandlerCalls())
        {
            yield return handlerCall.HandlerType;

            foreach (var parameter in handlerCall.Method.GetParameters()) yield return parameter.ParameterType;

            // Don't have to consider dependencies of a static handler
            if (handlerCall.HandlerType.IsStatic()) continue;
            
            var @default = container.Model.For(handlerCall.HandlerType).Default;
            foreach (var dependency in @default.Instance.Dependencies) yield return dependency.ServiceType;
        }
    }
}