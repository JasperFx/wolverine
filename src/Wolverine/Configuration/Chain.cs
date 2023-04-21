using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Attributes;
using Wolverine.Logging;
using Wolverine.Middleware;

namespace Wolverine.Configuration;

public interface IModifyChain<T> where T : IChain
{
    void Modify(T chain, GenerationRules rules);
}

public abstract class Chain<TChain, TModifyAttribute> : IChain
    where TChain : Chain<TChain, TModifyAttribute>
    where TModifyAttribute : Attribute, IModifyChain<TChain>
{
    private readonly List<Type> _dependencies = new();
    public List<Frame> Middleware { get; } = new();

    public List<Frame> Postprocessors { get; } = new();

    public Dictionary<string, object> Tags { get; } = new();

    public abstract string Description { get; }
    public List<AuditedMember> AuditedMembers { get; } = new();
    public abstract bool ShouldFlushOutgoingMessages();
    public abstract bool RequiresOutbox();

    public abstract MethodCall[] HandlerCalls();

    public void AddDependencyType(Type type)
    {
        _dependencies.Add(type);
    }

    public IReturnVariableActionSource ReturnVariableActionSource { get; set; } = new CascadingMessageActionSource();

    /// <summary>
    ///     Find all of the service dependencies of the current chain
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    public IEnumerable<Type> ServiceDependencies(IContainer container)
    {
        return serviceDependencies(container).Concat(_dependencies).Distinct();
    }

    public abstract bool HasAttribute<T>() where T : Attribute;
    public abstract Type? InputType();

    /// <summary>
    ///     Add a member of the message type to be audited during execution
    /// </summary>
    /// <param name="member"></param>
    /// <param name="heading"></param>
    public void Audit(MemberInfo member, string? heading = null)
    {
        AuditedMembers.Add(new AuditedMember(member, heading ?? member.Name,
            member.Name.SplitPascalCase().Replace(" ", ".").ToLowerInvariant()));
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
            if (handlerCall.HandlerType.IsStatic())
            {
                continue;
            }

            var @default = container.Model.For(handlerCall.HandlerType).Default;
            foreach (var dependency in @default.Instance.Dependencies) yield return dependency.ServiceType;
        }
    }

    protected void applyImpliedMiddlewareFromHandlers(GenerationRules generationRules)
    {
        var handlerTypes = HandlerCalls().Select(x => x.HandlerType).Distinct();
        foreach (var handlerType in handlerTypes)
        {
            var befores = MiddlewarePolicy.FilterMethods<WolverineBeforeAttribute>(handlerType.GetMethods(),
                MiddlewarePolicy.BeforeMethodNames);

            foreach (var before in befores)
            {
                var frame = new MethodCall(handlerType, before);
                MiddlewarePolicy.AssertMethodDoesNotHaveDuplicateReturnValues(frame);

                Middleware.Add(frame);

                // Potentially add handling for IResult or HandlerContinuation
                if (generationRules.TryFindContinuationHandler(frame, out var continuation))
                {
                    Middleware.Add(continuation!);
                }
            }

            var afters = MiddlewarePolicy.FilterMethods<WolverineAfterAttribute>(handlerType.GetMethods(),
                MiddlewarePolicy.AfterMethodNames);

            foreach (var after in afters)
            {
                var frame = new MethodCall(handlerType, after);
                Postprocessors.Add(frame);
            }
        }
    }
}