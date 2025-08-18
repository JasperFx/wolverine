using System.Reflection;
using System.Security.Claims;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Logging;
using Wolverine.Middleware;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

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
    public abstract void ApplyParameterMatching(MethodCall call);
    public List<Frame> Middleware { get; } = [];

    public abstract MiddlewareScoping Scoping { get; }

    public List<Frame> Postprocessors { get; } = [];

    [IgnoreDescription]
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
    public IEnumerable<Type> ServiceDependencies(IServiceContainer container, IReadOnlyList<Type> stopAtTypes)
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
        AuditedMembers.Add(new AuditedMember(member, heading ?? member.Name,
            member.Name.SplitPascalCase().Replace(' ', '.').ToLowerInvariant()));
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
        foreach (var property in type.GetProperties())
        {
            if (property.TryGetAttribute<AuditAttribute>(out var ratt))
            {
                Audit(property, ratt.Heading);
            }
        }

        foreach (var field in type.GetFields())
        {
            if (field.TryGetAttribute<AuditAttribute>(out var ratt))
            {
                Audit(field, ratt.Heading);
            }
        }
    }

    protected void applyAttributesAndConfigureMethods(GenerationRules rules, IServiceContainer container)
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
        {
            attribute.Modify(this, rules, container);
        }

        tryApplyResponseAware();
    }
    

    /// <summary>
    ///     Find all variables returned by any handler call in this chain
    ///     that can be cast to T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public IEnumerable<Variable> ReturnVariablesOfType<T>()
    {
        return HandlerCalls().SelectMany(x => x.Creates).Where(x => x.VariableType.CanBeCastTo<T>());
    }
    
    /// <summary>
    ///     Find all variables returned by any handler call in this chain
    ///     that can be cast to the supplied type
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Variable> ReturnVariablesOfType(Type interfaceType)
    {
        return HandlerCalls().SelectMany(x => x.Creates).Where(x => x.VariableType.CanBeCastTo(interfaceType));
    }

    public abstract bool TryFindVariable(string valueName, ValueSource source, Type valueType, out Variable variable);
    public abstract Frame[] AddStopConditionIfNull(Variable variable);

    public virtual Frame[] AddStopConditionIfNull(Variable data, Variable? identity, IDataRequirement requirement)
    {
        return AddStopConditionIfNull(data);
    }

    private static Type[] _typesToIgnore = new Type[]
    {
        typeof(DateOnly),
        typeof(TimeSpan),
        typeof(DateTimeOffset),
        typeof(BinaryReader),
        typeof(BinaryWriter),
        typeof(ClaimsIdentity),
        typeof(ClaimsPrincipal),
        typeof(Guid),
        typeof(byte[]),
        typeof(decimal),
    };
    
    private static bool isMaybeServiceDependency(Type type)
    {
        if (type.IsPrimitive) return false;
        if (type.IsSimple()) return false;
        if (type.IsDateTime()) return false;

        if (_typesToIgnore.Contains(type)) return false;

        if (type.IsNullable())
        {
            var innerType = type.GenericTypeArguments[0];
            if (_typesToIgnore.Contains(innerType)) return false;
            if (innerType.IsPrimitive) return false;
            if (innerType.IsSimple()) return false;
            if (innerType.IsDateTime()) return false;
        }

        if (type.IsArray)
        {
            return isMaybeServiceDependency(type.GetElementType());
        }

        if (ServiceContainer.IsEnumerable(type))
        {
            var elementType = type.GetGenericArguments()[0];
            return isMaybeServiceDependency(elementType);
        }

        if (type.IsInNamespace("System.Web")) return false;
        if (type.IsInNamespace("Microsoft.AspNetCore.Http")) return false;
        
        return true;
    }

    private IEnumerable<Type> serviceDependencies(IServiceContainer container, IReadOnlyList<Type> stopAtTypes)
    {
        var calls = Middleware.OfType<MethodCall>().Concat(HandlerCalls());

        foreach (var call in calls)
        {
            yield return call.HandlerType;

            if (!call.Method.IsStatic)
            {
                foreach (var type in container.ServiceDependenciesFor(call.HandlerType))
                {
                    yield return type;
                }
            }

            foreach (var parameter in call.Method.GetParameters())
            {
                // Absolutely do NOT let the dependency discovery go into the command/input/request types
                if (parameter.ParameterType != InputType() && isMaybeServiceDependency(parameter.ParameterType))
                {
                    yield return parameter.ParameterType;

                    if (stopAtTypes.Contains(parameter.ParameterType))
                    {
                        continue;
                    }

                    if (parameter.ParameterType.Assembly != GetType().Assembly ||
                        !stopAtTypes.Contains(parameter.ParameterType))
                    {
                        foreach (var dependencyType in container.ServiceDependenciesFor(parameter.ParameterType))
                        {
                            yield return dependencyType;
                        }
                    }
                }
            }

            // Don't have to consider dependencies of a static handler
            if (call.HandlerType.IsStatic())
            {
                continue;
            }
        }
    }

    private bool _appliedImpliedMiddleware;
    
    public void ApplyImpliedMiddlewareFromHandlers(GenerationRules generationRules)
    {
        if (_appliedImpliedMiddleware) return;
        _appliedImpliedMiddleware = true;
        
        var handlerTypes = HandlerCalls().Select(x => x.HandlerType).Distinct();
        foreach (var handlerType in handlerTypes)
        {
            var befores = MiddlewarePolicy.FilterMethods<WolverineBeforeAttribute>(this, handlerType.GetMethods(),
                MiddlewarePolicy.BeforeMethodNames);

            foreach (var before in befores)
            {
                var frame = new MethodCall(handlerType, before);
                MiddlewarePolicy.AssertMethodDoesNotHaveDuplicateReturnValues(frame);

                Middleware.Add(frame);

                // TODO -- might generalize this a bit. Have a more generic mode of understanding return values
                // like the HTTP support has
                var outgoings = frame.Creates.Where(x => x.VariableType == typeof(OutgoingMessages)).ToArray();
                int start = 100;
                foreach (var outgoing in outgoings)
                {
                    outgoing.OverrideName(outgoing.Usage + (++start));
                    Middleware.Add(new CaptureCascadingMessages(outgoing));
                }

                // Potentially add handling for IResult or HandlerContinuation
                if (generationRules.TryFindContinuationHandler(this, frame, out var continuation))
                {
                    Middleware.Add(continuation!);
                }
            }

            var afters = MiddlewarePolicy.FilterMethods<WolverineAfterAttribute>(this, handlerType.GetMethods(),
                MiddlewarePolicy.AfterMethodNames).ToArray();

            if (afters.Any())
            {
                for (int i = 0; i < afters.Length; i++)
                {
                    var frame = new MethodCall(handlerType, afters[i]);
                    Postprocessors.Insert(i, frame);
                }
            }
        }
    }

    public abstract void UseForResponse(MethodCall methodCall);

    protected internal void tryApplyResponseAware()
    {
        var responseAwares = ReturnVariablesOfType(typeof(IResponseAware)).ToArray();
        if (responseAwares.Length == 0) return;
        if (responseAwares.Length > 1)
            throw new InvalidOperationException(
                $"Cannot use more than one IResponseAware policy per chain. Chain {this} has {responseAwares.Select(x => x.ToString()).Join(", ")}");

        typeof(Applier<>).CloseAndBuildAs<IApplier>(this, responseAwares[0].VariableType).Apply();

        responseAwares[0]
            .UseReturnAction(new CommentReturnAction(
                $"{responseAwares[0].VariableType.FullNameInCode()} generates special response handling"));
    }
    
    public void AssertServiceLocationsAreAllowed(ServiceLocationReport[] reports, IServiceProvider? services)
    {
        if (!reports.Any()) return;
        
        var logger = services.GetLoggerOrDefault<ICodeFile>();
        var options = services.GetService<WolverineOptions>() ?? new WolverineOptions();

        switch (options.ServiceLocationPolicy)
        {
            case ServiceLocationPolicy.AllowedButWarn:
                foreach (var report in reports)
                {
                    if (report.ServiceDescriptor.IsKeyedService)
                    {
                        logger.LogInformation("Utilizing service location for {Chain} for Service {ServiceType} ({Key}): {Reason}. See https://wolverinefx.net/guide/codegen.html", Description, report.ServiceDescriptor.ServiceType, report.ServiceDescriptor.ServiceKey, report.Reason);
                    }
                    else
                    {
                        logger.LogInformation("Utilizing service location for {Chain} for Service {ServiceType}: {Reason}. See https://wolverinefx.net/guide/codegen.html", Description, report.ServiceDescriptor.ServiceType, report.Reason);
                    }
                }
                break;
            
            case ServiceLocationPolicy.NotAllowed:
                throw new InvalidServiceLocationException(this, reports);
            
            default:
                return;
        }


    }
}

public class InvalidServiceLocationException : Exception
{
    public static string ToMessage(IChain chain, ServiceLocationReport[] reports)
    {
        var writer = new StringWriter();
        writer.WriteLine($"Found service locations while generating code for {chain.Description}, but the policy is configured as {nameof(WolverineOptions)}.{nameof(WolverineOptions.ServiceLocationPolicy)} = {ServiceLocationPolicy.NotAllowed}");
        writer.WriteLine("See https://wolverinefx.net/guide/codegen.html for more information");
        writer.WriteLine("Service location(s):");
        foreach (var report in reports)
        {
            if (report.ServiceDescriptor.IsKeyedService)
            {
                writer.WriteLine($"Service {report.ServiceDescriptor.ServiceType.FullNameInCode()} ({report.ServiceDescriptor.ServiceKey}): {report.Reason}");
            }
            else
            {
                writer.WriteLine($"Service {report.ServiceDescriptor.ServiceType.FullNameInCode()}: {report.Reason}");
            }
            
        }

        return writer.ToString();
    }
    
    public InvalidServiceLocationException(IChain chain, ServiceLocationReport[] reports) : base(ToMessage(chain, reports))
    {
    }
}



internal interface IApplier
{
    void Apply();
}

internal class Applier<T> : IApplier where T : IResponseAware
{
    private readonly IChain _chain;

    public Applier(IChain chain)
    {
        _chain = chain;
    }


    public void Apply()
    {
        T.ConfigureResponse(_chain);
    }
}