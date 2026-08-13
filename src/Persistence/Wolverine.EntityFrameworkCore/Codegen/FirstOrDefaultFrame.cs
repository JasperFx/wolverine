using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     Emits <c>await dbContext.Set&lt;T&gt;().FirstOrDefaultAsync(token)</c> for a
///     <see cref="Wolverine.Persistence.FirstOrDefaultAttribute" /> parameter.
/// </summary>
/// <remarks>
///     The extension class and method are referenced through <c>typeof</c> / <c>nameof</c> rather than as literal
///     strings so that a rename in EF Core breaks this build instead of shipping a codegen failure that only
///     surfaces the first time an endpoint using the attribute is compiled at runtime.
/// </remarks>
internal class FirstOrDefaultFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly Type _entityType;
    private Variable? _context;
    private Variable? _cancellation;

    public FirstOrDefaultFrame(Type dbContextType, Type entityType)
    {
        _dbContextType = dbContextType;
        _entityType = entityType;
        Result = new Variable(entityType, $"firstOrDefault_{entityType.Name}", this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"Read the first {_entityType.NameInCode()} in the database, if any");
        writer.Write(
            $"var {Result.Usage} = await {typeof(EntityFrameworkQueryableExtensions).FullNameInCode()}.{nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)}({_context!.Usage}.{nameof(DbContext.Set)}<{_entityType.FullNameInCode()}>(), {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}
