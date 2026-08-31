using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     The load frame for a <c>[FromEfCore]</c> parameter that asked for eager loading or no-tracking. Emits
///     <c>dbContext.Set&lt;T&gt;()</c> composed with <c>AsNoTracking()</c> and the requested string
///     <c>Include(...)</c> paths, terminated by <c>FirstOrDefaultAsync(...)</c> on the primary key.
/// </summary>
/// <remarks>
///     <para>
///         The plain <see cref="LoadEntityFrame" /> uses <c>DbContext.FindAsync</c>, which is the better read for a
///         bare load — it can answer from the change tracker without touching the database. But <c>FindAsync</c>
///         accepts neither <c>Include</c> nor <c>AsNoTracking</c>, so honoring either one means changing the query
///         shape entirely. <c>[FromEfCore]</c> switches to this frame only when one of them is actually requested,
///         so an attribute with no extras still gets the cheaper <c>FindAsync</c>.
///     </para>
///     <para>
///         The key predicate goes through <c>EF.Property&lt;T&gt;(entity, "Name")</c> rather than a member access so
///         that a shadow primary key — one with no CLR property to write a lambda against — works identically to a
///         mapped one.
///     </para>
///     <para>
///         Extension classes and methods are referenced through <c>typeof</c> / <c>nameof</c> rather than string
///         literals so a rename in EF Core breaks this build, matching <see cref="AllFrame" /> and
///         <see cref="FirstOrDefaultFrame" />.
///     </para>
/// </remarks>
internal class LoadEntityWithQueryOptionsFrame : AsyncFrame
{
    // Deliberately obscure: a lambda parameter may not shadow a local in the enclosing method, and the
    // generated method is full of user-named locals.
    private const string LambdaParameter = "__entity";

    private readonly Type _dbContextType;
    private readonly Type _entityType;
    private readonly Variable _id;
    private readonly string _keyPropertyName;
    private readonly Type _keyType;
    private readonly string[] _includes;
    private readonly bool _asNoTracking;
    private Variable? _context;
    private Variable? _cancellation;

    public LoadEntityWithQueryOptionsFrame(Type dbContextType, Type entityType, Variable id, string keyPropertyName,
        Type keyType, string[] includes, bool asNoTracking)
    {
        _dbContextType = dbContextType;
        _entityType = entityType;
        _id = id;
        _keyPropertyName = keyPropertyName;
        _keyType = keyType;
        _includes = includes;
        _asNoTracking = asNoTracking;

        Entity = new Variable(entityType, this);
    }

    public Variable Entity { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var entityTypeName = _entityType.FullNameInCode();
        var extensions = typeof(EntityFrameworkQueryableExtensions).FullNameInCode();

        var query = $"{_context!.Usage}.{nameof(DbContext.Set)}<{entityTypeName}>()";

        if (_asNoTracking)
        {
            query =
                $"{extensions}.{nameof(EntityFrameworkQueryableExtensions.AsNoTracking)}<{entityTypeName}>({query})";
        }

        foreach (var include in _includes)
        {
            query =
                $"{extensions}.{nameof(EntityFrameworkQueryableExtensions.Include)}<{entityTypeName}>({query}, \"{include}\")";
        }

        var predicate =
            $"{LambdaParameter} => {typeof(EF).FullNameInCode()}.{nameof(EF.Property)}<{_keyType.FullNameInCode()}>({LambdaParameter}, \"{_keyPropertyName}\") == {_id.Usage}";

        writer.WriteLine("");
        writer.WriteComment(describe());
        writer.Write(
            $"var {Entity.Usage} = await {extensions}.{nameof(EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)}<{entityTypeName}>({query}, {predicate}, {_cancellation!.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }

    private string describe()
    {
        var text = $"Loading {_entityType.NameInCode()} by its primary key";
        if (_asNoTracking)
        {
            text += ", without change tracking";
        }

        if (_includes.Length > 0)
        {
            text += ", eagerly loading " + string.Join(", ", _includes.Select(x => "\"" + x + "\""));
        }

        return text;
    }
}
