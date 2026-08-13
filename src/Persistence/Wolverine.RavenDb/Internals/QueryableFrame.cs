using System.Diagnostics.CodeAnalysis;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals;

/// <summary>
///     Exposes RavenDb's raw <c>IQueryable&lt;T&gt;</c> for a
///     <see cref="Wolverine.Persistence.QueryableAttribute" /> parameter.
/// </summary>
internal class QueryableFrame : SyncFrame
{
    private readonly Type _elementType;
    private Variable? _source;

    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericType closes IReadOnlyList<>/IQueryable<> over the element type at CODEGEN time only. AOT consumers run pre-generated code in TypeLoadMode.Static, so this never fires in a published app. See the AOT guide.")]
    public QueryableFrame(Type elementType)
    {
        _elementType = elementType;
        Result = new Variable(typeof(IQueryable<>).MakeGenericType(elementType), $"queryable_{elementType.Name}",
            this);
    }

    public Variable Result { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"The raw RavenDb IQueryable for {_elementType.NameInCode()}");
        writer.Write(
            $"{Result.VariableType.FullNameInCode()} {Result.Usage} = {_source!.Usage}.Query<{_elementType.FullNameInCode()}>();");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _source = chain.FindVariable(typeof(IAsyncDocumentSession));
        yield return _source;
    }
}
