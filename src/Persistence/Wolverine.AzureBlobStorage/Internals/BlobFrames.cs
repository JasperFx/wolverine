using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.AzureBlobStorage.Internals;

/// <summary>
/// Resolves the tenant to pass into <see cref="BlobStorageActionApplier" />.
/// </summary>
/// <remarks>
/// Same order the Marten, Polecat and Fisher session frames use — the chain's own <c>tenantId</c>
/// variable first, then the active message context — so a blob document and a Marten document in one
/// handler resolve the same tenant.
/// </remarks>
internal class BlobTenantSource
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
/// Loads a registered document out of Azure Blob Storage, or null when the blob is not there.
/// </summary>
internal class LoadBlobDocumentFrame : AsyncFrame
{
    private readonly Variable _id;
    private readonly BlobTenantSource _tenant = new();
    private Variable? _cancellation;
    private Variable? _session;

    public LoadBlobDocumentFrame(Type documentType, Variable id)
    {
        _id = id;
        Document = new Variable(documentType, this);
    }

    public Variable Document { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IBlobDocumentSession));
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
        writer.WriteComment(
            $"Load the {Document.VariableType.NameInCode()} from Azure Blob Storage, or null if it is not there");
        writer.Write(
            $"var {Document.Usage} = await {typeof(BlobStorageActionApplier).FullNameInCode()}.{nameof(BlobStorageActionApplier.LoadAsync)}<{Document.VariableType.FullNameInCode()}>({_session!.Usage}, {_id.Usage}, {_tenant.Expression}, {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Calls one of the <see cref="BlobStorageActionApplier" /> write methods against a variable the
/// handler already has — an <c>IStorageAction&lt;T&gt;</c>, or the document itself.
/// </summary>
internal class BlobWriteFrame : AsyncFrame
{
    private readonly Type _documentType;
    private readonly string _methodName;
    private readonly BlobTenantSource _tenant = new();
    private readonly Variable _value;
    private Variable? _cancellation;
    private Variable? _session;

    public BlobWriteFrame(string methodName, Type documentType, Variable value)
    {
        _methodName = methodName;
        _documentType = documentType;
        _value = value;

        uses.Add(value);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IBlobDocumentSession));
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
            $"await {typeof(BlobStorageActionApplier).FullNameInCode()}.{_methodName}<{_documentType.FullNameInCode()}>({_session!.Usage}, {_value.Usage}, {_tenant.Expression}, {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}
