using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Middleware;

namespace Wolverine.Runtime.Handlers;

public class HandlerChain : Chain<HandlerChain, ModifyHandlerChainAttribute>, IWithFailurePolicies, ICodeFile
{
    public const string HandlerSuffix = "Handler";
    public const string ConsumerSuffix = "Consumer";
    public const string Handle = "Handle";
    public const string Handles = "Handles";
    public const string Consume = "Consume";
    public const string Consumes = "Consumes";
    public const string NotCascading = "NotCascading";

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

        foreach (var property in messageType.GetProperties())
        {
            if (property.TryGetAttribute<AuditAttribute>(out var att))
            {
                Audit(property, att.Heading);
            }
        }
        
        foreach (var field in messageType.GetFields())
        {
            if (field.TryGetAttribute<AuditAttribute>(out var att))
            {
                Audit(field, att.Heading);
            }
        }
    }


    private HandlerChain(MethodCall call, HandlerGraph parent) : this(call.Method.MessageType()!, parent)
    {
        Handlers.Add(call);
    }

    public override bool HasAttribute<T>()
    {
        return Handlers.Any(x => x.Method.HasAttribute<T>() || x.HandlerType.HasAttribute<T>());
    }

    public override Type? InputType() => MessageType;

    public HandlerChain(IGrouping<Type, HandlerCall> grouping, HandlerGraph parent) : this(grouping.Key, parent)
    {
        Handlers.AddRange(grouping);

        var i = 0;

        foreach (var handler in Handlers)
        foreach (var create in handler.Creates)
            i = DisambiguateOutgoingVariableName(create, i);
    }

    public IEnumerable<Type> PublishedTypes()
    {
        var ignoredTypes = new Type[]
        {
            typeof(object),
            typeof(object[]),
            typeof(IEnumerable<object>),
            typeof(IList<object>),
            typeof(IReadOnlyList<object>),
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

    string ICodeFile.FileName => TypeName + ".cs";

    /// <summary>
    ///     Configure the retry policies and error handling for this chain
    /// </summary>
    public FailureRuleCollection Failures { get; } = new();

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        _generatedType = assembly.AddType(TypeName, typeof(MessageHandler));

        foreach (var handler in Handlers) assembly.ReferenceAssembly(handler.HandlerType.Assembly);

        var handleMethod = _generatedType.MethodFor(nameof(MessageHandler.HandleAsync));
        handleMethod.Sources.Add(new MessageHandlerVariableSource(MessageType));
        handleMethod.Sources.Add(new LoggerVariableSource(MessageType));
        handleMethod.Frames.AddRange(DetermineFrames(assembly.Rules, _parent.Container!));

        // TODO -- this is temporary, but there's a bug in LamarCodeGeneration that uses await using
        // when the method returns IAsyncDisposable
        handleMethod.AsyncMode = AsyncMode.AsyncTask;

        handleMethod.DerivedVariables.Add(new Variable(typeof(IMessageContext), "context"));
        handleMethod.DerivedVariables.Add(new Variable(typeof(IMessageBus), "context"));

        handleMethod.DerivedVariables.Add(new Variable(typeof(Envelope),
            $"context.{nameof(IMessageContext.Envelope)}"));
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider services,
        string containingNamespace)
    {
        var found = this.As<ICodeFile>().AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider services,
        string containingNamespace)
    {
        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == TypeName);

        if (_handlerType == null)
        {
            return false;
        }

        Handler = (MessageHandler)services.As<IContainer>().QuickBuild(_handlerType);
        Handler.Chain = this;
        
        Debug.WriteLine(_generatedType?.SourceCode);

        return true;
    }

    public static HandlerChain For<T>(Expression<Action<T>> expression, HandlerGraph parent)
    {
        var method = ReflectionHelper.GetMethod(expression);
        var call = new MethodCall(typeof(T), method);

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
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    internal virtual List<Frame> DetermineFrames(GenerationRules rules, IContainer container)
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

        applyCustomizations(rules, container);

        var cascadingHandlers = determineCascadingMessages().ToArray();

        // The Enqueue cascading needs to happen before the post processors because of the
        // transactional & outbox support
        return Middleware.Concat(Handlers).Concat(cascadingHandlers).Concat(Postprocessors).ToList();
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

    private IEnumerable<CaptureCascadingMessages> determineCascadingMessages()
    {
        foreach (var handler in Handlers)
        foreach (var create in handler.Creates)
        {
            if (!create.ShouldBeCascaded())
            {
                continue;
            }

            yield return new CaptureCascadingMessages(create);
        }
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