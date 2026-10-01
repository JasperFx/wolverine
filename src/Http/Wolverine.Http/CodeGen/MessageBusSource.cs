using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Http.Runtime;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class MessageBusSource : IVariableSource
{
    private readonly bool _relayUserName;
    private CreateMessageContextWithMaybeTenantFrame? _frame;

    /// <param name="relayUserName">
    /// <see cref="WolverineOptions.EnableRelayOfUserName" />. Read once here, at the point where the
    /// source is added to the HTTP chain's generated method, because the frame itself has no access to
    /// WolverineOptions and there is nothing per-chain about the decision.
    /// </param>
    public MessageBusSource(bool relayUserName)
    {
        _relayUserName = relayUserName;
    }

    public bool Matches(Type type)
    {
        return type == typeof(IMessageBus) || type == typeof(IMessageContext) || type == typeof(MessageContext);
    }

    public Variable Create(Type type)
    {
        // Cache the frame so a chain that resolves both IMessageContext (e.g. from
        // OpenMartenSessionFrame's non-tenant fallback) and MessageContext (e.g. from
        // FlushOutgoingMessages' MethodCall target) shares a single frame instead of
        // emitting `var messageContext = new MessageContext(_wolverineRuntime);` twice.
        // The cache is per-source, and MessageBusSource is registered per HTTP chain in
        // HttpChain.Codegen.cs, so the scope is exactly one generated handler method.
        //
        // We always return the concrete MessageContext variable rather than the
        // CastVariable for the matched interface. The interface CastVariables exist for
        // downstream parameter-strategy users (UseMessageBusFrame), but consumers via
        // chain.TryFindVariable(IMessageContext) — including the Wolverine.Marten
        // CreateDocumentSessionFrame — invoke methods on the concrete MessageContext
        // (e.g. EnqueueCascadingAsync, FlushOutgoingMessagesAsync) that are not visible
        // through the interface. Pre-fix behavior was equivalent because each Create
        // call produced a fresh CreateMessageContextWithMaybeTenantFrame whose .Variable
        // was the only thing ever handed out from this source.
        _frame ??= new CreateMessageContextWithMaybeTenantFrame(_relayUserName);
        return _frame.Variable;
    }
}

internal class CreateMessageContextWithMaybeTenantFrame : SyncFrame
{
    private readonly bool _relayUserName;
    private Variable? _tenantId;
    private Variable? _runtime;
    private Variable? _httpContext;
    public CastVariable IMessageContextVariable { get; }
    public CastVariable IMessageBusVariable { get; }

    public CreateMessageContextWithMaybeTenantFrame(bool relayUserName)
    {
        _relayUserName = relayUserName;
        Variable = new Variable(typeof(MessageContext), this);
        IMessageContextVariable = new CastVariable(Variable, typeof(IMessageContext));
        creates.Add(IMessageContextVariable);
        IMessageBusVariable = new CastVariable(Variable, typeof(IMessageBus));
        creates.Add(IMessageBusVariable);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = new {typeof(MessageContext).FullNameInCode()}({_runtime!.Usage});");
        if (_tenantId != null)
        {
            writer.Write($"{Variable.Usage}.{nameof(IMessageBus.TenantId)} = {_tenantId.Usage};");
        }

        if (_httpContext != null)
        {
            writer.WriteComment(
                "Relay the authenticated user name onto the message context before anything else can use it");
            writer.Write(
                $"{typeof(UserNameMiddleware).FullNameInCode()}.{nameof(UserNameMiddleware.Apply)}({_httpContext.Usage}, {Variable.Usage});");
        }

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{Variable.FSharpAssignmentUsage} = {typeof(MessageContext).FSharpName()}({_runtime!.FSharpUsage})");
        if (_tenantId != null)
        {
            writer.Write($"{Variable.FSharpUsage}.{nameof(IMessageBus.TenantId)} <- {_tenantId.FSharpUsage}");
        }

        if (_httpContext != null)
        {
            writer.Write(
                $"{typeof(UserNameMiddleware).FSharpName()}.{nameof(UserNameMiddleware.Apply)}({_httpContext.FSharpUsage}, {Variable.FSharpUsage})");
        }

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _runtime = chain.FindVariable(typeof(IWolverineRuntime));
        yield return _runtime;

        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out _tenantId))
        {
            yield return _tenantId;
        }

        // GH-4741. The user name relay has to happen here rather than from a policy-inserted middleware
        // frame. A store session is opened by its own IVariableSource frame (Wolverine.Marten's
        // SessionVariableSource and the Polecat/Fisher equivalents), and JasperFx's MethodFrameArranger
        // hoists every variable-source frame to the front of the method BEFORE the topological sort --
        // so nothing inserted at chain.Middleware[0] can precede the OpenSession(messageContext) call,
        // and the eager copy of context.UserName into IDocumentSession.LastModifiedBy inside
        // OutboxedSessionFactory always read a null. This frame IS the one that produces the
        // MessageContext those session frames depend on, so the sort guarantees it runs first.
        if (_relayUserName)
        {
            _httpContext = chain.FindVariable(typeof(HttpContext));
            yield return _httpContext;
        }
    }
}