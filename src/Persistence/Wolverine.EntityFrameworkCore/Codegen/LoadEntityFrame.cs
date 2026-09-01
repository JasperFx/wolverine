using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;

namespace Wolverine.EntityFrameworkCore.Codegen;

internal class LoadEntityFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly Variable _sagaId;
    private readonly bool _compositeKey;
    private Variable? _cancellation;
    private Variable? _context;
    private Variable? _tenantId;

    public LoadEntityFrame(Type dbContextType, Type sagaType, Variable sagaId)
    {
        _dbContextType = dbContextType;
        _sagaId = sagaId;

        // GH-3542. A partitioned conjoined saga has a REAL composite (TenantId, Id) key in the EF model --
        // unlike every other partitioned entity, whose composite exists only in the database -- because saga
        // ids are app-assigned and could otherwise collide across tenants. FindAsync takes composite key
        // values in key order, and that order is (Id, TenantId) -- the saga's own id leads, because
        // Wolverine determines the saga id type from the primary key and leading with the tenant makes it
        // assign the saga id into TenantId. See ConjoinedTenancyModelCustomizer.applyCompositeSagaKeys.
        _compositeKey = ConjoinedTenancy.OptionsFor(dbContextType).PartitioningEnabled
                        && sagaType.CanBeCastTo<ITenanted>();

        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (_compositeKey)
        {
            _tenantId = chain.FindVariable(typeof(TenantId));
            yield return _tenantId;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Trying to load the existing Saga data");

        var keyValues = _compositeKey
            ? $"{_sagaId.Usage}, {_tenantId!.Usage}.{nameof(TenantId.Value)}"
            : _sagaId.Usage;

        writer.Write(
            $"var {Saga.Usage} = await {_context!.Usage}.{nameof(DbContext.FindAsync)}<{Saga.VariableType.FullNameInCode()}>({keyValues}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}
