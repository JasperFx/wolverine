using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

/// <summary>
///     GH-5345. Sets <see cref="Saga.LastModified" /> to <see cref="DateTimeOffset.UtcNow" /> immediately before
///     the wrapped persistence frame inserts or updates the saga. Doing it here, in the generated code around
///     whatever frame the persistence provider supplied, means every saga store (Marten, Polecat, Fisher, EF Core,
///     the lightweight database tables, RavenDb, CosmosDb, Redis, the in-memory store...) stamps the value the same
///     way and at the same point, and user code cannot forget or fake it.
/// </summary>
internal class StampSagaLastModifiedFrame : Frame
{
    private readonly Frame _inner;
    private readonly Variable _saga;
    private readonly bool _guardNull;

    /// <param name="saga">The saga variable about to be persisted</param>
    /// <param name="inner">The provider's insert or update frame</param>
    /// <param name="guardNull">
    ///     True when the saga is a value a handler returned, which may legitimately be null. The saga chain's
    ///     own saga variable is never null at the point it is persisted.
    /// </param>
    public StampSagaLastModifiedFrame(Variable saga, Frame inner, bool guardNull = false) : base(inner.IsAsync)
    {
        _saga = saga;
        _inner = inner;
        _guardNull = guardNull;
        uses.Add(saga);
    }

    internal static void WriteStamp(ISourceWriter writer, Variable saga, bool guardNull = false)
    {
        // Cast to Saga so a subclass that declares its own LastModified (hiding the base one) cannot capture it
        var assignment =
            $"(({typeof(Saga).FullName}){saga.Usage}).{nameof(Saga.LastModified)} = {typeof(DateTimeOffset).FullName}.{nameof(DateTimeOffset.UtcNow)};";

        writer.Write(guardNull ? $"if ({saga.Usage} != null) {assignment}" : assignment);
    }

    internal static void WriteFSharpStamp(ISourceWriter writer, Variable saga, bool guardNull = false)
    {
        var assignment =
            $"({saga.Usage} :> {typeof(Saga).FullName}).{nameof(Saga.LastModified)} <- {typeof(DateTimeOffset).FullName}.{nameof(DateTimeOffset.UtcNow)}";

        writer.Write(guardNull ? $"if not (isNull (box {saga.Usage})) then {assignment}" : assignment);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        WriteStamp(writer, _saga, _guardNull);
        _inner.GenerateCode(method, writer);

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        WriteFSharpStamp(writer, _saga, _guardNull);
        _inner.GenerateFSharpCode(method, writer);

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _saga;
        foreach (var variable in _inner.FindVariables(chain)) yield return variable;
    }
}
