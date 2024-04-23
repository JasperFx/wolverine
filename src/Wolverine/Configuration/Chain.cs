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
    private readonly List<Type> _dependencies = [];
    public List<Frame> Middleware { get; } = [];

    public List<Frame> Postprocessors { get; } = [];

    public Dictionary<string, object> Tags { get; } = new();

    public abstract string Description { get; }
    public List<AuditedMember> AuditedMembers { get; } = [];
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
    /// <param name="container"></param>
    /// <param name="stopAtTypes"></param>
    /// <param name="chain"></param>
    /// <returns></returns>
    public IEnumerable<Type> ServiceDependencies(IContainer container, IReadOnlyList<Type> stopAtTypes)
    {
        return serviceDependencies(container, stopAtTypes).Concat(_dependencies).Distinct();
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
        AuditedMembers.Add(AuditedMember.Create(member, heading));
    }

    private bool isConfigureMethod(MethodInfo method)
    {
        if (method.Name != "Configure")
        {
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != 1)
        {
            return false;
        }

        return typeof(TChain).CanBeCastTo(parameters.Single().ParameterType);
    }


    protected void applyAuditAttributes(Type type)
    {
        foreach (var auditedMember in AuditedMember.GetAllFromType(type))
        {
            AuditedMembers.Add(auditedMember);
        }
    }
    
    protected void applyAttributesAndConfigureMethods(GenerationRules rules, IContainer container)
    {
        var handlers = HandlerCalls();
        var configureMethods = handlers.Select(x => x.HandlerType).Distinct()
            .SelectMany(x => x.GetMethods())
            .Where(isConfigureMethod);

        foreach (var method in configureMethods) method.Invoke(null, [this]);

        var handlerAtts = handlers.SelectMany(x => x.HandlerType
            .GetCustomAttributes<TModifyAttribute>());

        var methodAtts = handlers.SelectMany(x => x.Method.GetCustomAttributes<TModifyAttribute>());

        foreach (var attribute in handlerAtts.Concat(methodAtts)) attribute.Modify(this.As<TChain>(), rules);

        var genericHandlerAtts = handlers.SelectMany(x => x.HandlerType
            .GetCustomAttributes<ModifyChainAttribute>());

        var genericMethodAtts = handlers.SelectMany(x => x.Method.GetCustomAttributes<ModifyChainAttribute>());

        foreach (var attribute in genericHandlerAtts.Concat(genericMethodAtts))
            attribute.Modify(this, rules, container);
    }

    private IEnumerable<Type> serviceDependencies(IContainer container, IReadOnlyList<Type> stopAtTypes)
    {
        var calls = Middleware.OfType<MethodCall>().Concat(HandlerCalls());
        
        foreach (var call in calls)
        {
            yield return call.HandlerType;

            foreach (var parameter in call.Method.GetParameters())
            {
                // Absolutely do NOT let Lamar go into the command/input/request types
                if (parameter.ParameterType != InputType() && !parameter.ParameterType.IsPrimitive)
                {
                    yield return parameter.ParameterType;

                    if (parameter.ParameterType.Assembly != GetType().Assembly ||
                        !stopAtTypes.Contains(parameter.ParameterType))
                    {
                        var candidate = container.Model.For(parameter.ParameterType).Default;
                        if (candidate != null)
                        {
                            foreach (var dependency in candidate.Instance.Dependencies)
                                yield return dependency.ServiceType;
                        }
                    }
                }
            }

            // Don't have to consider dependencies of a static handler
            if (call.HandlerType.IsStatic())
            {
                continue;
            }

            var @default = container.Model.For(call.HandlerType).Default;
            if (@default != null)
            {
                foreach (var dependency in @default.Instance.Dependencies) yield return dependency.ServiceType;
            }
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