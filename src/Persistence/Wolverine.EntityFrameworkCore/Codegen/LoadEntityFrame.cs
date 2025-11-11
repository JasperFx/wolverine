using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Codegen;

internal class LoadEntityFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _context;

    public LoadEntityFrame(Type dbContextType, Type sagaType, Variable sagaId)
    {
        _dbContextType = dbContextType;
        _sagaId = sagaId;

        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Trying to load the existing Saga data");
        writer.Write(
            $"var {Saga.Usage} = await {_context!.Usage}.{nameof(DbContext.FindAsync)}<{Saga.VariableType.FullNameInCode()}>({_sagaId.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}