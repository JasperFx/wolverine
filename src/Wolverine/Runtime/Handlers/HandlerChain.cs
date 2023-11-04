using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;

namespace Wolverine.Runtime.Handlers;

internal class ContextVariable : Variable
{
    public ContextVariable(Type variableType) : base(variableType, "context")
    {
    }

    public override void OverrideName(string variableName)
    {
        // Do nothing here!
    }
}

public class HandlerChain : Chain<HandlerChain, ModifyHandlerChainAttribute>, IWithFailurePolicies, ICodeFile
{
    public const string HandlerSuffix = "Handler";
    public const string ConsumerSuffix = "Consumer";
    public const string Handle = "Handle";
    public const string Handles = "Handles";
    public const string Consume = "Consume";
    public const string Consumes = "Consumes";

    private readonly HandlerGraph _parent;

    public readonly List<MethodCall> Handlers = new();
    private GeneratedType? _generatedType;
    private Type? _handlerType;

    private bool _hasConfiguredFrames;

    public HandlerChain(Type messageType, HandlerGraph parent)
    {
        _parent = parent;
        MessageType = messageType ?? throw new ArgumentNullException(nameof(messageType));

        TypeName = messageType.ToSuffixedTypeName(HandlerSuffix);

        Description = "Message Handler for " + MessageType.FullNameInCode();

        applyAuditAttributes(messageType);
    }


    private HandlerChain(MethodCall call, HandlerGraph parent) : this(call.Method.MessageType()!, parent)
    {
        Handlers.Add(call);
    }

    public HandlerChain(IGrouping<Type, HandlerCall> grouping, HandlerGraph parent) : this(grouping.Key, parent)
    {
        Handlers.AddRange(grouping);

        var i = 0;

        foreach (var handler in Handlers)
        foreach (var create in handler.Creates)
            i = DisambiguateOutgoingVariableName(create, i);
    }

    /// <summary>
    ///     At what level should Wolverine log messages about messages succeeding? The default
    ///     is Information
    /// </summary>
    [Obsolete("The naming is misleading, please use SuccessLogLevel")]
    public LogLevel ExecutionLogLevel
    {
        get => SuccessLogLevel;
        set => SuccessLogLevel = value;
    }
    
    /// <summary>
    ///     At what level should Wolverine log messages of this type about messages succeeding? The default
    ///     is Information
    /// </summary>
    public LogLevel SuccessLogLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// At what level should processing starting and finishing be logged for this message type?
    /// The default is Debug
    /// </summary>
    public LogLevel ProcessingLogLevel { get; set; } = LogLevel.Debug;

    /// <summary>
    /// Is Open Telemetry logging enabled during message invocation (IMessageBus.InvokeAsync()) for
    /// this message type. Default is true
    /// </summary>
    public bool TelemetryEnabled { get; set; } = true;

    /// <summary>
    ///     A textual description of this HandlerChain
    /// </summary>
    public override string Description { get; }

    public Type MessageType { get; }

    /// <summary>
    ///     Wolverine's string identification for this message type
    /// </summary>
    public string TypeName { get; }

    internal MessageHandler? Handler { get; private set; }

    /// <summary>
    ///     The message execution timeout in seconds for this specific message type. This uses a CancellationTokenSource
    ///     behind the scenes, and the timeout enforcement is dependent on the usage within handlers
    /// </summary>
    public int? ExecutionTimeoutInSeconds { get; set; }

    internal string? SourceCode => _generatedType?.SourceCode;

    string ICodeFile.FileName => TypeName;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        foreach (var handler in Handlers)
        {
            if (handler.Creates.Any(x => x.VariableType == typeof(Envelope)))
            {
                throw new InvalidHandlerException(
                    $"Invalid Wolverine handler signature. Method {handler} creates a {typeof(Envelope).FullNameInCode()}");
            }
        }

        _generatedType = assembly.AddType(TypeName, typeof(MessageHandler));

        foreach (var handler in Handlers) assembly.ReferenceAssembly(handler.HandlerType.Assembly);

        var handleMethod = _generatedType.MethodFor(nameof(MessageHandler.HandleAsync));

        handleMethod.Sources.Add(new LoggerVariableSource(MessageType));
        var envelopeVariable = new Variable(typeof(Envelope),
            $"context.{nameof(IMessageContext.Envelope)}");

        var messageVariable = new MessageVariable(envelopeVariable, InputType());

        var frames = DetermineFrames(assembly.Rules, _parent.Container!, messageVariable);
        var index = 0;
        foreach (var variable in frames.SelectMany(x => x.Creates))
        {
            // Might wanna make this more generic later. Some kind of "reserved" variable names?
            if (variable.Usage == "context")
            {
                variable.OverrideName("context" + ++index);
            }
        }

        handleMethod.Frames.Insert(0, messageVariable.Creator!);
        handleMethod.Frames.AddRange(frames);

        if (frames.Any(x => x.IsAsync))
        {
            handleMethod.AsyncMode = AsyncMode.AsyncTask;
        }
        
        handleMethod.DerivedVariables.Add(new ContextVariable(typeof(IMessageContext)));
        handleMethod.DerivedVariables.Add(new ContextVariable(typeof(IMessageBus)));


        handleMethod.DerivedVariables.Add(envelopeVariable);
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        var found = this.As<ICodeFile>().AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        // TEMP!
        Debug.WriteLine(_generatedType?.SourceCode);
        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == TypeName);

        if (_handlerType == null)
        {
            return false;
        }

        Handler = (MessageHandler)services!.As<IContainer>().QuickBuild(_handlerType);
        Handler.Chain = this;

        Debug.WriteLine(_generatedType?.SourceCode);

        return true;
    }

    /// <summary>
    ///     Configure the retry policies and error handling for this chain
    /// </summary>
    public FailureRuleCollection Failures { get; } = new();

    public override bool HasAttribute<T>()
    {
        return Handlers.Any(x => x.Method.HasAttribute<T>() || x.HandlerType.HasAttribute<T>());
    }

    public override Type InputType()
    {
        return MessageType;
    }

    public IEnumerable<Type> PublishedTypes()
    {
        var ignoredTypes = new[]
        {
            typeof(object),
            typeof(object[]),
            typeof(IEnumerable<object>),
            typeof(IList<object>),
            typeof(IReadOnlyList<object>)
        };

        foreach (var variable in Handlers.SelectMany(x => x.Creates))
        {
            if (ignoredTypes.Contains(variable.VariableType) ||
                variable.VariableType.CanBeCastTo<IEnumerable<object>>())
            {
                continue;
            }

            yield return variable.VariableType;
        }
    }

    public static HandlerChain For<T>(Expression<Action<T>> expression, HandlerGraph parent)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method!);

        return new HandlerChain(call, parent);
    }

    public static HandlerChain For<T>(string methodName, HandlerGraph parent)
    {
        var handlerType = typeof(T);
        var method = handlerType.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);

        if (method == null)
        {
            throw new ArgumentOutOfRangeException(nameof(methodName),
                $"Cannot find method named '{methodName}' in type {handlerType.FullName}");
        }

        var call = new MethodCall(handlerType, method)
        {
            CommentText = "Core message handling method"
        };

        return new HandlerChain(call, parent);
    }

    internal MessageHandler CreateHandler(IContainer container)
    {
        if (_handlerType == null)
        {
            throw new InvalidOperationException("The handler type has not been built yet");
        }

        var handler = container.QuickBuild(_handlerType).As<MessageHandler>();
        handler.Chain = this;
        Handler = handler;

        return handler;
    }

    /// <summary>
    ///     Used internally to create the initial list of ordered Frames
    ///     that will be used to generate the MessageHandler
    /// </summary>
    /// <param name="rules"></param>
    /// <param name="container"></param>
    /// <param name="messageVariable"></param>
    /// <param name="messageVariable"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    internal virtual List<Frame> DetermineFrames(GenerationRules rules, IContainer container, MessageVariable messageVariable)
    {
        if (!Handlers.Any())
        {
            throw new InvalidOperationException("No method handlers configured for message type " +
                                                MessageType.FullName);
        }

        if (AuditedMembers.Any())
        {
            Middleware.Insert(0, new AuditToActivityFrame(this));
        }
        
        Middleware.Insert(0, messageVariable.Creator!);

        applyCustomizations(rules, container);

        var handlerReturnValueFrames = determineHandlerReturnValueFrames().ToArray();

        // Allow for immutable message types that get overwritten by middleware
        foreach (var methodCall in Middleware.OfType<MethodCall>())
        {
            methodCall.TryReplaceVariableCreationWithAssignment(messageVariable);
        }

        // The Enqueue cascading needs to happen before the post processors because of the
        // transactional & outbox support
        return Middleware.Concat(Handlers).Concat(handlerReturnValueFrames).Concat(Postprocessors).ToList();
    }

    protected void applyCustomizations(GenerationRules rules, IContainer container)
    {
        if (!_hasConfiguredFrames)
        {
            _hasConfiguredFrames = true;

            applyAttributesAndConfigureMethods(rules, container);

            foreach (var attribute in MessageType.GetTypeInfo()
                         .GetCustomAttributes(typeof(ModifyHandlerChainAttribute))
                         .OfType<ModifyHandlerChainAttribute>()) attribute.Modify(this, rules);

            foreach (var attribute in MessageType.GetTypeInfo().GetCustomAttributes(typeof(ModifyChainAttribute))
                         .OfType<ModifyChainAttribute>()) attribute.Modify(this, rules, container);
        }

        applyImpliedMiddlewareFromHandlers(rules);
    }

    protected IEnumerable<Frame> determineHandlerReturnValueFrames()
    {
        return Handlers.SelectMany(x => x.Creates)
            .Select(x => x.ReturnAction(this))
            .SelectMany(x => x.Frames());
    }

    internal static int DisambiguateOutgoingVariableName(Variable create, int i)
    {
        create.OverrideName("outgoing" + ++i);

        return i;
    }

    public override bool RequiresOutbox()
    {
        return true;
    }

    public override MethodCall[] HandlerCalls()
    {
        return Handlers.ToArray();
    }

    public override string ToString()
    {
        return
            $"{MessageType.NameInCode()} handled by {Handlers.Select(x => $"{x.HandlerType.NameInCode()}.{x.Method.Name}()").Join(", ")}";
    }

    public override bool ShouldFlushOutgoingMessages()
    {
        return false;
    }

    internal TimeSpan DetermineMessageTimeout(WolverineOptions options)
    {
        if (ExecutionTimeoutInSeconds.HasValue)
        {
            return ExecutionTimeoutInSeconds.Value.Seconds();
        }

        return options.DefaultExecutionTimeout;
    }
}