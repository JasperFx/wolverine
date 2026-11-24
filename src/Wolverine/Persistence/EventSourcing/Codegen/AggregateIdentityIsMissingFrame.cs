using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.EventSourcing.Codegen;

/// <summary>
///     GH-5947. Whether the identity a write loads its aggregate by is missing from the request: null, an empty string,
///     an empty Guid, or a strong typed id or natural key wrapping one of those. Only placed in a chain that answers a
///     missing identity itself (an HTTP endpoint, with a 400); a message handler leaves it to the load, which throws.
/// </summary>
internal class AggregateIdentityIsMissingFrame : SyncFrame
{
    private readonly Variable _identity;
    private readonly string _check;
    private readonly string _fsharpCheck;

    private AggregateIdentityIsMissingFrame(Variable identity, string name, string check, string fsharpCheck)
    {
        _identity = identity;
        _check = check;
        _fsharpCheck = fsharpCheck;
        uses.Add(identity);

        IsMissing = new Variable(typeof(bool), $"{name}_IdentityIsMissing", this);
    }

    public Variable IsMissing { get; }

    /// <summary>
    ///     The frame for this identity, or null when no value of its type means "missing", such as an int.
    /// </summary>
    /// <param name="name">Names the generated variable: the aggregate's parameter, unique on the chain</param>
    public static AggregateIdentityIsMissingFrame? For(Variable identity, string name)
    {
        var declaredType = declaredTypeOf(identity);
        var check = buildCheck(identity.Usage, declaredType, false);
        if (check == null) return null;

        return new AggregateIdentityIsMissingFrame(identity, name, check, buildCheck(identity.FSharpUsage, declaredType, true)!);
    }

    // A member's own type: GetMemberType(), behind a MemberAccessVariable's type, strips Nullable<T>
    private static Type declaredTypeOf(Variable variable) => variable switch
    {
        MemberAccessVariable { Member: PropertyInfo property } => property.PropertyType,
        MemberAccessVariable { Member: FieldInfo field } => field.FieldType,
        _ => variable.VariableType
    };

    private static string? buildCheck(string usage, Type type, bool fsharp)
    {
        if (type == typeof(string))
        {
            return $"System.String.IsNullOrEmpty({usage})";
        }

        if (type == typeof(Guid))
        {
            return fsharp ? $"{usage} = System.Guid.Empty" : $"{usage} == System.Guid.Empty";
        }

        var isNull = fsharp ? $"isNull (box {usage})" : $"{usage} is null";

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            var inner = buildCheck($"{usage}.Value", underlying, fsharp);
            return inner == null ? isNull : $"{isNull} || {inner}";
        }

        // A strong typed id or natural key: missing when it wraps a missing value. A key with no single inner value
        // is missing only when it's null.
        string? wrapped = null;
        if (type != typeof(object) && StrongTypedIdResolver.TryResolve(type, out var info, StrongTypedIdStyle.AnyType))
        {
            wrapped = buildCheck($"{usage}.{info.ValueProperty.Name}", info.ValueProperty.PropertyType, fsharp);
        }

        if (type.IsValueType) return wrapped;

        return wrapped == null ? isNull : $"{isNull} || {wrapped}";
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5947: a write with no identity to load by is refused before the load");
        writer.Write($"var {IsMissing.Usage} = {_check};");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5947: a write with no identity to load by is refused before the load");
        writer.WriteLine($"let {IsMissing.Usage} = {_fsharpCheck}");
        Next?.GenerateFSharpCode(method, writer);
    }
}
