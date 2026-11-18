using Wolverine;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace MartenTests.Workflows.Engine;

/// <summary>
/// GH-5654 SPIKE. One [DurableWorkflow] method, plus what startup can learn about it without a source generator:
/// the message types it receives (found by reading the method's IL for InvokeAsync&lt;T&gt; / WaitForAsync&lt;T&gt;
/// calls) and an IL fingerprint.
/// </summary>
public class WorkflowDefinition
{
    public WorkflowDefinition(MethodInfo method)
    {
        if (!method.IsStatic)
        {
            throw new ArgumentOutOfRangeException(nameof(method),
                $"A [DurableWorkflow] must be a static method, but {method.DeclaringType!.Name}.{method.Name} is not");
        }

        if (method.ReturnType != typeof(Task))
        {
            throw new ArgumentOutOfRangeException(nameof(method), "A [DurableWorkflow] method must return Task");
        }

        var att = method.GetCustomAttribute<DurableWorkflowAttribute>() ?? new DurableWorkflowAttribute();

        Method = method;
        Name = att.Name ?? $"{method.DeclaringType!.Name}.{method.Name}";
        Version = att.Version;

        var parameters = method.GetParameters();
        if (parameters.Length < 2 || parameters[1].ParameterType != typeof(IWorkflowContext))
        {
            throw new ArgumentOutOfRangeException(nameof(method),
                "A [DurableWorkflow] method takes (triggering message, IWorkflowContext, services...)");
        }

        TriggerType = parameters[0].ParameterType;
        ServiceTypes = parameters.Skip(2).Select(x => x.ParameterType).ToArray();

        var body = WorkflowIl.BodyOf(method);
        Fingerprint = WorkflowIl.Fingerprint(body);
        ReceivedTypes = WorkflowIl.ReceivedMessageTypes(body).ToArray();

        // The trigger has to name the workflow itself. (The real build would also refuse a WaitForAsync<T> whose T
        // has no workflow id member; replies to InvokeAsync come back through the saga-id header instead.)
        if (!WorkflowIdentity.CanCorrelate(TriggerType))
        {
            throw new ArgumentOutOfRangeException(nameof(method),
                $"The triggering message {TriggerType.Name} of workflow {Name} needs a [SagaIdentity] or Id member");
        }
    }

    public MethodInfo Method { get; }
    public string Name { get; }
    public int Version { get; }
    public Type TriggerType { get; }
    public Type[] ServiceTypes { get; }
    public Type[] ReceivedTypes { get; }
    public string Fingerprint { get; }

    public Task InvokeAsync(object trigger, IWorkflowContext context, IServiceProvider services)
    {
        var args = new object?[2 + ServiceTypes.Length];
        args[0] = trigger;
        args[1] = context;
        for (var i = 0; i < ServiceTypes.Length; i++)
        {
            args[i + 2] = services.GetService(ServiceTypes[i]) ??
                          throw new InvalidOperationException(
                              $"Workflow {Name} needs a {ServiceTypes[i].FullNameInCode()}, but none is registered");
        }

        return (Task)Method.Invoke(null, BindingFlags.DoNotWrapExceptions, null, args, null)!;
    }

    public override string ToString() => $"{Name} v{Version}";
}

public static class WorkflowIdentity
{
    /// <summary>
    /// The saga rules: a [SagaIdentity] member or an Id member on the message, else the saga-id header (which is
    /// how replies to InvokeAsync come back).
    /// </summary>
    public static Guid? Find(object message, Envelope? envelope)
    {
        var member = memberFor(message.GetType());
        var value = member?.GetValue(message);
        switch (value)
        {
            case Guid g when g != Guid.Empty:
                return g;
            case string s when Guid.TryParse(s, out var parsed):
                return parsed;
        }

        if (envelope?.SagaId != null && Guid.TryParse(envelope.SagaId, out var fromHeader)) return fromHeader;

        return null;
    }

    public static bool CanCorrelate(Type messageType) => memberFor(messageType) != null;

    private static PropertyInfo? memberFor(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return properties.FirstOrDefault(x => x.HasAttribute<SagaIdentityAttribute>())
               ?? properties.FirstOrDefault(x => x.Name == "Id");
    }
}

/// <summary>Just enough IL reading to find the workflow's received types and fingerprint it</summary>
internal static class WorkflowIl
{
    private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(x => (OpCode)x.GetValue(null)!)
        .ToDictionary(x => x.Value);

    /// <summary>An async method's real body is its state machine's MoveNext()</summary>
    public static MethodInfo BodyOf(MethodInfo method)
    {
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        return stateMachine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
               ?? method;
    }

    public static string Fingerprint(MethodInfo body)
    {
        // Raw IL including metadata tokens: an unrelated edit elsewhere in the assembly can shift a token and
        // change this hash. Fine for an informational "definition changed" flag; see the spike report.
        var il = body.GetMethodBody()?.GetILAsByteArray() ?? [];
        return Convert.ToHexString(SHA256.HashData(il))[..16];
    }

    public static IEnumerable<Type> ReceivedMessageTypes(MethodInfo body)
    {
        return calledMethods(body)
            .OfType<MethodInfo>()
            .Where(x => x.DeclaringType == typeof(IWorkflowContext) && x.IsGenericMethod &&
                        x.Name is nameof(IWorkflowContext.InvokeAsync) or nameof(IWorkflowContext.WaitForAsync))
            .Select(x => x.GetGenericArguments()[0])
            .Distinct();
    }

    private static IEnumerable<MethodBase> calledMethods(MethodInfo body)
    {
        var il = body.GetMethodBody()?.GetILAsByteArray();
        if (il == null) yield break;

        var position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE) value = unchecked((short)(0xFE00 | il[position++]));

            var code = _opCodes[value];
            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, position);
                    position += 4 + 4 * count;
                    break;
                case OperandType.InlineMethod:
                    var token = BitConverter.ToInt32(il, position);
                    position += 4;
                    MethodBase? called = null;
                    try
                    {
                        called = body.Module.ResolveMethod(token, body.DeclaringType?.GetGenericArguments(),
                            body.GetGenericArguments());
                    }
                    catch (ArgumentException)
                    {
                        // Not resolvable in this context; not one of ours
                    }

                    if (called != null) yield return called;
                    break;
                default:
                    position += 4;
                    break;
            }
        }
    }
}

/// <summary>Every workflow definition known to the application</summary>
public class WorkflowRegistry
{
    private readonly Dictionary<string, WorkflowDefinition> _byNameAndVersion = new();
    private readonly Dictionary<Type, WorkflowDefinition> _byTrigger = new();

    public WorkflowRegistry(IEnumerable<WorkflowDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            var key = keyFor(definition.Name, definition.Version);
            if (_byNameAndVersion.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"Two [DurableWorkflow] methods are named {definition.Name} version {definition.Version}: {existing.Method.DeclaringType!.Name}.{existing.Method.Name} and {definition.Method.DeclaringType!.Name}.{definition.Method.Name}");
            }

            _byNameAndVersion[key] = definition;

            // New instances start on the highest version
            if (!_byTrigger.TryGetValue(definition.TriggerType, out var current) || current.Version < definition.Version)
            {
                _byTrigger[definition.TriggerType] = definition;
            }
        }
    }

    public IEnumerable<WorkflowDefinition> Definitions => _byNameAndVersion.Values;

    public IEnumerable<Type> HandledMessageTypes()
    {
        return Definitions.SelectMany(x => x.ReceivedTypes.Append(x.TriggerType))
            .Concat([typeof(WorkflowTimeout), typeof(WorkflowDelay)])
            .Distinct();
    }

    public bool TryFindStarter(Type messageType, out WorkflowDefinition definition)
    {
        return _byTrigger.TryGetValue(messageType, out definition!);
    }

    public bool TryFind(string name, int version, out WorkflowDefinition definition)
    {
        return _byNameAndVersion.TryGetValue(keyFor(name, version), out definition!);
    }

    private static string keyFor(string name, int version) => $"{name}/v{version}";
}
