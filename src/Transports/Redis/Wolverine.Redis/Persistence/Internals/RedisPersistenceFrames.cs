using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Redis.Internal;

/// <summary>
/// Resolves the tenant to pass into <see cref="RedisStorageActionApplier" />.
/// </summary>
/// <remarks>
/// Same order the Marten, Polecat, Fisher and S3 session frames use — the chain's own <c>tenantId</c>
/// variable first, then the active message context — so a Redis document and a Marten document in one
/// handler resolve the same tenant.
/// </remarks>
internal class RedisTenantSource
{
    private Variable? _context;
    private Variable? _tenantId;

    public IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out var tenant))
        {
            _tenantId = tenant;
            yield return _tenantId;
            yield break;
        }

        _context = chain.TryFindVariable(typeof(IMessageContext), VariableSource.NotServices)
                   ?? chain.TryFindVariable(typeof(IMessageBus), VariableSource.NotServices);

        if (_context != null)
        {
            yield return _context;
        }
    }

    public string Expression =>
        _tenantId?.Usage ?? (_context == null ? "null" : $"{_context.Usage}.{nameof(IMessageBus.TenantId)}");
}

/// <summary>
/// Shared plumbing for every frame here: the session, the cancellation token and the tenant.
/// </summary>
internal abstract class RedisFrame : AsyncFrame
{
    private readonly RedisTenantSource _tenant = new();
    private Variable? _cancellation;
    private Variable? _session;

    protected string Session => _session!.Usage;
    protected string Cancellation => _cancellation!.Usage;
    protected string Tenant => _tenant.Expression;

    protected static string Applier => typeof(RedisStorageActionApplier).FullNameInCode();

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IRedisDocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        foreach (var variable in _tenant.FindVariables(chain))
        {
            yield return variable;
        }
    }
}

/// <summary>
/// Loads a registered document out of Redis, or null when the key is not there.
/// </summary>
internal class LoadRedisDocumentFrame : RedisFrame
{
    private readonly Variable _id;

    public LoadRedisDocumentFrame(Type documentType, Variable id)
    {
        _id = id;
        Document = new Variable(documentType, this);
    }

    public Variable Document { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Load the {Document.VariableType.NameInCode()} from Redis, or null if it is not there");
        writer.Write(
            $"var {Document.Usage} = await {Applier}.{nameof(RedisStorageActionApplier.LoadAsync)}<{Document.VariableType.FullNameInCode()}>({Session}, {_id.Usage}, {Tenant}, {Cancellation}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Loads a saga together with the revision its eventual write has to match, so that
/// <see cref="RedisSagaUpdateFrame" /> and <see cref="RedisSagaDeleteFrame" /> can be compare-and-swap.
/// </summary>
internal class LoadRedisSagaFrame : RedisFrame
{
    private readonly Variable _sagaId;

    public LoadRedisSagaFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;
        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    /// <summary>
    /// Name of the local carrying the revision the saga was read at. Derived from the saga variable so
    /// that two loads in one method cannot collide.
    /// </summary>
    public static string VersionVariableName(Variable saga)
    {
        return $"{saga.Usage}_Version";
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var state = $"{Saga.Usage}_State";

        writer.WriteLine("");
        writer.WriteComment("Load the saga from Redis with the revision to compare the eventual write against");
        writer.Write(
            $"var {state} = await {Applier}.{nameof(RedisStorageActionApplier.LoadSagaAsync)}<{Saga.VariableType.FullNameInCode()}>({Session}, {_sagaId.Usage}, {Tenant}, {Cancellation}).ConfigureAwait(false);");
        writer.Write($"{Saga.VariableType.FullNameInCode()} {Saga.Usage} = {state}.Saga;");
        writer.Write($"string {VersionVariableName(Saga)} = {state}.Version;");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Creates a saga, refusing if one already exists at the key.
/// </summary>
internal class RedisSagaInsertFrame : RedisFrame
{
    private readonly Variable _saga;

    public RedisSagaInsertFrame(Variable saga)
    {
        _saga = saga;
        uses.Add(saga);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {Applier}.{nameof(RedisStorageActionApplier.InsertSagaAsync)}<{_saga.VariableType.FullNameInCode()}>({Session}, {_saga.Usage}, {Tenant}, {Cancellation}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Writes a saga only if the stored revision is still the one the message read.
/// </summary>
internal class RedisSagaUpdateFrame : RedisFrame
{
    private readonly Variable _saga;

    public RedisSagaUpdateFrame(Variable saga)
    {
        _saga = saga;
        uses.Add(saga);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {Applier}.{nameof(RedisStorageActionApplier.UpdateSagaAsync)}<{_saga.VariableType.FullNameInCode()}>({Session}, {_saga.Usage}, {LoadRedisSagaFrame.VersionVariableName(_saga)}, {Tenant}, {Cancellation}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Deletes a completed saga only if the stored revision is still the one the message read.
/// </summary>
internal class RedisSagaDeleteFrame : RedisFrame
{
    private readonly Variable _saga;
    private readonly Variable _sagaId;

    public RedisSagaDeleteFrame(Variable sagaId, Variable saga)
    {
        _sagaId = sagaId;
        _saga = saga;
        uses.Add(sagaId);
        uses.Add(saga);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {Applier}.{nameof(RedisStorageActionApplier.DeleteSagaAsync)}<{_saga.VariableType.FullNameInCode()}>({Session}, {_sagaId.Usage}, {LoadRedisSagaFrame.VersionVariableName(_saga)}, {Tenant}, {Cancellation}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Calls one of the <see cref="RedisStorageActionApplier" /> write methods against a variable the
/// handler already has — an <c>IStorageAction&lt;T&gt;</c>, or the document itself.
/// </summary>
internal class RedisWriteFrame : RedisFrame
{
    private readonly Type _documentType;
    private readonly string _methodName;
    private readonly Variable _value;

    public RedisWriteFrame(string methodName, Type documentType, Variable value)
    {
        _methodName = methodName;
        _documentType = documentType;
        _value = value;

        uses.Add(value);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {Applier}.{_methodName}<{_documentType.FullNameInCode()}>({Session}, {_value.Usage}, {Tenant}, {Cancellation}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}
