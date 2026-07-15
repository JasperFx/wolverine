using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class MessageBusSource : IVariableSource
{
    private CreateMessageContextWithMaybeTenantFrame? _frame;

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
        _frame ??= new CreateMessageContextWithMaybeTenantFrame();
        return _frame.Variable;
    }
}

internal class CreateMessageContextWithMaybeTenantFrame : SyncFrame
{
    private Variable? _tenantId;
    private Variable? _runtime;
    public CastVariable IMessageContextVariable { get; }
    public CastVariable IMessageBusVariable { get; }

    public CreateMessageContextWithMaybeTenantFrame()
    {
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

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"{Variable.FSharpAssignmentUsage} = {typeof(MessageContext).FSharpName()}({_runtime!.FSharpUsage})");
        if (_tenantId != null)
        {
            writer.Write($"{Variable.FSharpUsage}.{nameof(IMessageBus.TenantId)} <- {_tenantId.FSharpUsage}");
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
    }
}