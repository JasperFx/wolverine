using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;

namespace Wolverine.Marten.Codegen;

/// <summary>
///     Exposes Marten's raw <c>IQueryable&lt;T&gt;</c> for a
///     <see cref="Wolverine.Persistence.QueryableAttribute" /> parameter.
/// </summary>
internal class QueryableFrame : SyncFrame
{
    private readonly Type _elementType;
    private Variable? _source;

    public QueryableFrame(Type elementType)
    {
        _elementType = elementType;
        Result = new Variable(typeof(IQueryable<>).MakeGenericType(elementType), $"queryable_{elementType.Name}",
            this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"The raw Marten IQueryable for {_elementType.NameInCode()}");
        writer.Write(
            $"{Result.VariableType.FullNameInCode()} {Result.Usage} = {_source!.Usage}.{nameof(IQuerySession.Query)}<{_elementType.FullNameInCode()}>();");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _source = chain.FindVariable(typeof(IQuerySession));
        yield return _source;
    }
}
