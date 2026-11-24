using System.Reflection;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.EventSourcing.Codegen;

/// <summary>
/// The natural key as the generated code passes it to the store. The fetch APIs take <c>TKey</c>, never
/// <c>TKey?</c>, so a member that can be null is checked first, the way a null string stream id is
/// treated: a write refuses it, and a read finds nothing.
/// </summary>
internal class NaturalKeyArgument
{
    private readonly Variable _member;
    private readonly bool _isNullableStruct;

    public NaturalKeyArgument(Variable member)
    {
        _member = member;

        var memberType = declaredTypeOf(member);
        var underlying = Nullable.GetUnderlyingType(memberType);
        _isNullableStruct = underlying != null;
        KeyType = underlying ?? memberType;
        CanBeNull = _isNullableStruct || !memberType.IsValueType;
    }

    /// <summary>
    /// A <see cref="MemberAccessVariable"/>, such as a member of an HTTP request body, takes its type from
    /// <c>GetMemberType()</c>, which strips <c>Nullable&lt;T&gt;</c>. The member's declared type keeps it.
    /// </summary>
    private static Type declaredTypeOf(Variable member) => member switch
    {
        MemberAccessVariable { Member: PropertyInfo property } => property.PropertyType,
        MemberAccessVariable { Member: FieldInfo field } => field.FieldType,
        _ => member.VariableType
    };

    /// <summary>
    /// <c>TKey</c>, the type argument for <c>FetchForWriting&lt;T, TKey&gt;</c> and the other fetch APIs.
    /// </summary>
    public Type KeyType { get; }

    public bool CanBeNull { get; }

    public string Usage => _isNullableStruct ? $"{_member.Usage}.Value" : _member.Usage;

    public string FSharpUsage => _isNullableStruct ? $"{_member.FSharpUsage}.Value" : _member.FSharpUsage;

    // "is null", not "== null": a key class with its own operator == would otherwise be called with the null,
    // or be ambiguous (CS0121) if it declares more than one
    public string IsNullCheck => $"{_member.Usage} is null";

    public string FSharpIsNullCheck => $"isNull (box {_member.FSharpUsage})";

    /// <summary>
    /// GH-5947 (E5): the <c>ParamName</c> of the exception for a null key is the member or value carrying the key, as
    /// in <c>Number</c>, rather than the generated expression that reads it.
    /// </summary>
    public string ParamName => _member switch
    {
        MemberAccessVariable { Member: { } member } => member.Name,
        MessageMemberVariable { Member: { } member } => member.Name,
        _ => _member.Usage
    };

    public string NullKeyMessage(Type aggregateType) =>
        $"The {KeyType.FullNameInCode()} natural key on this message is null, so there is no {aggregateType.FullNameInCode()} to load";

    /// <summary>
    /// A write has no aggregate to load and no key to claim. The exception every store throws for an empty Guid
    /// or a null string stream id, and Marten for a null natural key.
    /// </summary>
    public string ThrowIfNull(Type aggregateType) =>
        $"if ({IsNullCheck}) throw new System.ArgumentOutOfRangeException(\"{ParamName}\", \"{NullKeyMessage(aggregateType)}\");";

    public string FSharpThrowIfNull(Type aggregateType) =>
        $"if {FSharpIsNullCheck} then raise (System.ArgumentOutOfRangeException(\"{ParamName}\", \"{NullKeyMessage(aggregateType)}\"))";
}
