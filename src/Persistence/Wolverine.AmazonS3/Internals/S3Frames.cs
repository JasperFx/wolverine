using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.AmazonS3.Internals;

/// <summary>
/// Resolves the tenant to pass into <see cref="S3StorageActionApplier" />.
/// </summary>
/// <remarks>
/// Same order the Marten, Polecat and Fisher session frames use — the chain's own <c>tenantId</c>
/// variable first, then the active message context — so an S3 document and a Marten document in one
/// handler resolve the same tenant.
/// </remarks>
internal class S3TenantSource
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
/// Loads a registered document out of S3, or null when the object is not there.
/// </summary>
internal class LoadS3DocumentFrame : AsyncFrame
{
    private readonly Variable _id;
    private readonly S3TenantSource _tenant = new();
    private Variable? _cancellation;
    private Variable? _session;

    public LoadS3DocumentFrame(Type documentType, Variable id)
    {
        _id = id;
        Document = new Variable(documentType, this);
    }

    public Variable Document { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IS3DocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        foreach (var variable in _tenant.FindVariables(chain))
        {
            yield return variable;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Load the {Document.VariableType.NameInCode()} from S3, or null if it is not there");
        writer.Write(
            $"var {Document.Usage} = await {typeof(S3StorageActionApplier).FullNameInCode()}.{nameof(S3StorageActionApplier.LoadAsync)}<{Document.VariableType.FullNameInCode()}>({_session!.Usage}, {_id.Usage}, {_tenant.Expression}, {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Calls one of the <see cref="S3StorageActionApplier" /> write methods against a variable the handler
/// already has — an <c>IStorageAction&lt;T&gt;</c>, or the document itself.
/// </summary>
internal class S3WriteFrame : AsyncFrame
{
    private readonly Type _documentType;
    private readonly string _methodName;
    private readonly S3TenantSource _tenant = new();
    private readonly Variable _value;
    private Variable? _cancellation;
    private Variable? _session;

    public S3WriteFrame(string methodName, Type documentType, Variable value)
    {
        _methodName = methodName;
        _documentType = documentType;
        _value = value;

        uses.Add(value);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IS3DocumentSession));
        yield return _session;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        foreach (var variable in _tenant.FindVariables(chain))
        {
            yield return variable;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"await {typeof(S3StorageActionApplier).FullNameInCode()}.{_methodName}<{_documentType.FullNameInCode()}>({_session!.Usage}, {_value.Usage}, {_tenant.Expression}, {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}
