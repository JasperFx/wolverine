using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// GH-5556. The <c>Compensate</c> methods one saga type declares, and the rule that picks one for a failure
/// </summary>
internal sealed class SagaCompensationMethods
{
    private static readonly ConcurrentDictionary<Type, SagaCompensationMethods> _cache = new();

    private readonly CompensateMethod[] _methods;

    private SagaCompensationMethods(CompensateMethod[] methods)
    {
        _methods = methods;
    }

    public int Count => _methods.Length;

    public IEnumerable<T> Select<T>(Func<CompensateMethod, T> selector) => _methods.Select(selector);

    public bool Any(Func<CompensateMethod, bool> filter) => _methods.Any(filter);

    public IEnumerator<CompensateMethod> GetEnumerator() => ((IEnumerable<CompensateMethod>)_methods).GetEnumerator();

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Saga types are rooted by handler discovery; Compensate methods are public members of them.")]
    public static SagaCompensationMethods For(Type sagaType)
        => _cache.GetOrAdd(sagaType, type => new SagaCompensationMethods(
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.Name == SagaCompensations.Compensate || x.Name == SagaCompensations.Compensate + "Async")
                .Where(x => x.DeclaringType != typeof(object))
                .OrderBy(x => x.MetadataToken)
                .Select((x, i) => CompensateMethod.For(type, x, i))
                .ToArray()));

    /// <summary>
    /// The method that compensates a <paramref name="messageType" /> that failed with this exception: the most specific
    /// exception first, then the most specific message type, then a live exception over a <see cref="RemoteFailure{T}" />
    /// over <see cref="ExceptionInfo" />, then the order they are declared in. Null when none matches
    /// </summary>
    public CompensateMethod? Select(Type messageType, ExceptionInfo info, Exception? live)
    {
        CompensateMethod? best = null;
        (int, int, int, int) bestRank = default;

        foreach (var method in _methods)
        {
            if (!method.FailedType.IsAssignableFrom(messageType)) continue;
            if (method.ExceptionRank(info, live) is not { } exceptionRank) continue;

            var rank = (exceptionRank, distance(messageType, method.FailedType), (int)method.Kind, method.Order);
            if (best == null || rank.CompareTo(bestRank) < 0)
            {
                best = method;
                bestRank = rank;
            }
        }

        return best;
    }

    // How many steps up from the actual type to the declared one. An interface counts as further than any class
    private static int distance(Type actual, Type declared)
    {
        var steps = 0;
        for (var type = actual; type != null; type = type.BaseType)
        {
            if (type == declared) return steps;
            steps++;
        }

        return 1000;
    }
}

/// <summary>How a Compensate method takes the exception</summary>
internal enum CompensateExceptionKind
{
    /// <summary>The live exception: only for a failure in this process</summary>
    Live = 0,

    /// <summary><see cref="RemoteFailure{TException}" />, matched through <see cref="ExceptionInfo.TypeHierarchy" /></summary>
    Remote = 1,

    /// <summary><see cref="ExceptionInfo" />: any failure</summary>
    Info = 2
}

/// <summary>One Compensate method of a saga</summary>
internal sealed class CompensateMethod
{
    private CompensateMethod(MethodInfo method, Type failedType, CompensateExceptionKind kind, Type exceptionType,
        int order, Type[] namedTypes)
    {
        Method = method;
        FailedType = failedType;
        Kind = kind;
        ExceptionType = exceptionType;
        Order = order;
        NamedTypes = namedTypes;
        _exceptionName = exceptionType.FullName ?? exceptionType.Name;
    }

    private readonly string _exceptionName;

    public MethodInfo Method { get; }
    public Type FailedType { get; }
    public CompensateExceptionKind Kind { get; }
    public Type ExceptionType { get; }
    public int Order { get; }

    /// <summary>
    /// GH-5672. The message types <see cref="CompensatesAttribute" /> names on this method: messages the saga sends
    /// that Wolverine cannot see in any signature, such as one sent through <c>IMessageBus</c>
    /// </summary>
    public Type[] NamedTypes { get; }

    /// <summary>
    /// GH-5672. Is the failed-message parameter an interface or a base type, so the method compensates every concrete
    /// message type assignable to it rather than one exact type?
    /// </summary>
    public bool CoversAFamily => FailedType.IsInterface || FailedType.IsAbstract || FailedType == typeof(object) ||
                                 !FailedType.IsSealed;

    /// <summary>Can this method compensate a failed <paramref name="messageType" />?</summary>
    public bool Covers(Type messageType) => FailedType.IsAssignableFrom(messageType);

    /// <summary>A readable signature, for describe and for error messages</summary>
    public string Signature
        => $"{Method.Name}({string.Join(", ", Method.GetParameters().Take(2).Select(x => x.ParameterType.NameInCode()))})";

    public static CompensateMethod For(Type sagaType, MethodInfo method, int order)
    {
        var parameters = method.GetParameters();
        string where() => $"{sagaType.FullNameInCode()}.{method.Name}({string.Join(", ", parameters.Select(x => x.ParameterType.NameInCode()))})";

        if (parameters.Length < 2)
        {
            throw new InvalidSagaException(
                $"{where()} cannot compensate anything: a Compensate method takes the failed message, then the exception (an Exception type, RemoteFailure<TException> or ExceptionInfo)");
        }

        var failedType = parameters[0].ParameterType;
        if (failedType.IsValueType || SagaCompensations.FaultMessageType(failedType) != null)
        {
            throw new InvalidSagaException(
                $"{where()}: the first parameter is the failed message, a class that is not itself a Fault<T>");
        }

        var exceptionParameter = parameters[1].ParameterType;
        CompensateExceptionKind kind;
        Type exceptionType;
        if (exceptionParameter == typeof(ExceptionInfo))
        {
            kind = CompensateExceptionKind.Info;
            exceptionType = typeof(Exception);
        }
        else if (exceptionParameter.IsGenericType && exceptionParameter.GetGenericTypeDefinition() == typeof(RemoteFailure<>))
        {
            kind = CompensateExceptionKind.Remote;
            exceptionType = exceptionParameter.GetGenericArguments()[0];
        }
        else if (exceptionParameter.CanBeCastTo<Exception>())
        {
            kind = CompensateExceptionKind.Live;
            exceptionType = exceptionParameter;
        }
        else
        {
            throw new InvalidSagaException(
                $"{where()}: the second parameter is the exception, as an Exception type (a failure in this service), RemoteFailure<TException> or ExceptionInfo");
        }

        var namedTypes = method.GetCustomAttributes<CompensatesAttribute>().SelectMany(x => x.MessageTypes).Distinct()
            .ToArray();
        foreach (var named in namedTypes)
        {
            if (named.IsValueType || named.IsAbstract || named.IsInterface || SagaCompensations.FaultMessageType(named) != null)
            {
                throw new InvalidSagaException(
                    $"{where()} is marked [Compensates(typeof({named.NameInCode()}))], but [Compensates] names concrete message classes, not an interface, an abstract type or a Fault<T>");
            }

            if (!failedType.IsAssignableFrom(named))
            {
                throw new InvalidSagaException(
                    $"{where()} is marked [Compensates(typeof({named.NameInCode()}))], but a {named.NameInCode()} cannot be passed as its failed message, a {failedType.NameInCode()}");
            }
        }

        return new CompensateMethod(method, failedType, kind, exceptionType, order, namedTypes);
    }

    /// <summary>
    /// How closely this method's exception matches the failure, 0 being the thrown type itself. Null if it does not
    /// match at all
    /// </summary>
    public int? ExceptionRank(ExceptionInfo info, Exception? live)
    {
        switch (Kind)
        {
            case CompensateExceptionKind.Live:
                if (live == null || !ExceptionType.IsInstanceOfType(live)) return null;
                var steps = 0;
                for (var type = live.GetType(); type != null && type != ExceptionType; type = type.BaseType) steps++;
                return steps;

            case CompensateExceptionKind.Remote:
                if (ExceptionType == typeof(Exception)) return int.MaxValue - 1;
                if (info.Type == _exceptionName) return 0;
                for (var i = 0; i < info.TypeHierarchy.Count; i++)
                {
                    if (info.TypeHierarchy[i] == _exceptionName) return i;
                }

                return null;

            default:
                return int.MaxValue;
        }
    }

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "RemoteFailure<T> closed over the declared exception type of a rooted saga method.")]
    public async Task<object?> InvokeAsync(Saga saga, object failed, ExceptionInfo info, Exception? live,
        MessageContext context)
    {
        var parameters = Method.GetParameters();
        var arguments = new object?[parameters.Length];
        arguments[0] = failed;
        arguments[1] = Kind switch
        {
            CompensateExceptionKind.Live => live,
            CompensateExceptionKind.Remote => Activator.CreateInstance(
                typeof(RemoteFailure<>).MakeGenericType(ExceptionType), info),
            _ => info
        };

        for (var i = 2; i < parameters.Length; i++)
        {
            arguments[i] = resolve(parameters[i].ParameterType, context);
        }

        object? result;
        try
        {
            result = Method.Invoke(saga, arguments);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }

        return await unwrapAsync(result).ConfigureAwait(false);
    }

    private static object? resolve(Type type, MessageContext context)
    {
        if (type == typeof(CancellationToken)) return CancellationToken.None;
        if (type.IsInstanceOfType(context)) return context;
        if (type == typeof(Envelope)) return context.Envelope;
        if (type == typeof(IWolverineRuntime)) return context.Runtime;

        return context.Runtime.Services.GetRequiredService(type);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Task<T>.Result and ValueTask<T>.AsTask of a Compensate method's own return type.")]
    private static async Task<object?> unwrapAsync(object? result)
    {
        switch (result)
        {
            case null:
                return null;

            case Task task:
                await task.ConfigureAwait(false);
                var taskType = task.GetType();
                return taskType.IsGenericType && taskType.GetProperty("Result") is { } property &&
                       property.PropertyType.Name != "VoidTaskResult"
                    ? property.GetValue(task)
                    : null;

            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                return null;
        }

        var type = result.GetType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = (Task)type.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(result, null)!;
            return await unwrapAsync(asTask).ConfigureAwait(false);
        }

        return result;
    }
}
