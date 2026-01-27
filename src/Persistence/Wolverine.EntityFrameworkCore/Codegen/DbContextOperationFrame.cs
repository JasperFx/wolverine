using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.EntityFrameworkCore.Codegen;

internal class DbContextOperationFrame : SyncFrame
{
    private readonly Type _dbContextType;
    private readonly string _methodName;
    private readonly Variable _saga;
    private Variable? _context;

    public DbContextOperationFrame(Type dbContextType, Variable saga, string methodName)
    {
        _dbContextType = dbContextType;
        _saga = saga;
        _methodName = methodName;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment("Registering the Saga entity change");
        writer.Write($"{_context!.Usage}.{_methodName}({_saga.Usage});");
        Next?.GenerateCode(method, writer);
    }
}