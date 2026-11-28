using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Wolverine.Configuration.EventModeling;

/// <summary>
///     GH-6005 (jasperfx#1299). The reflection path for field shapes: the fields of a command, event, read model
///     or aggregate as <c>describe-event-model</c>, <c>event-model</c> and <c>scaffold</c> see them at describe
///     time. The source generator in <c>JasperFx.SourceGenerator</c> writes the same shapes into the event-model
///     manifest for the AOT path; the rules here mirror its, so the two agree field for field.
/// </summary>
/// <remarks>
///     <para>
///         <b>What a field is.</b> Every public instance property with a getter (not an indexer) and every public
///         instance field, base type first, in declaration order -- what a record's positional parameters become.
///     </para>
///     <para>
///         <b>What reflection sees.</b> <c>Nullable&lt;T&gt;</c> and a nullable reference annotation make the
///         field <see cref="FieldDescriptor.Optional" />; <c>[Required]</c> or the <c>required</c> modifier takes
///         that back. An array or an <c>IEnumerable&lt;T&gt;</c> is <see cref="FieldCardinality.Many" /> of its
///         element type. <c>[Identity]</c>, <c>[Key]</c>, a member named <c>Id</c> or named for its owner
///         (<c>OrderId</c> on <c>Order</c>) is the identity. A member whose type the store registered as a tag
///         carries that tag.
///     </para>
///     <para>
///         <b>What only an attribute can say.</b> <c>[Generated]</c> (or <c>[DatabaseGenerated]</c>) and
///         <c>[Pii]</c>. Nothing reads a method body to decide that a value is generated.
///     </para>
/// </remarks>
public static class EventModelShapes
{
    /// <summary>
    ///     The shapes of every type in <paramref name="types" /> that has fields worth modelling -- no primitives,
    ///     no strings, no enums, no interfaces, nothing from the framework -- on the Derived rung, each type once.
    /// </summary>
    public static IReadOnlyList<TypeShapeDescriptor> For(IEnumerable<Type> types, EventModelDerivation derivation)
    {
        var shapes = new List<TypeShapeDescriptor>();
        var seen = new HashSet<Type>();

        foreach (var type in types)
        {
            if (!IsShapeable(type) || !seen.Add(type)) continue;
            shapes.Add(Reflect(type, derivation));
        }

        return shapes;
    }

    /// <summary>
    ///     Can the fields of <paramref name="type" /> be modelled? A closed class, struct or record of the
    ///     application's own, not a primitive, string, enum, array, interface, delegate, or framework type.
    /// </summary>
    public static bool IsShapeable(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsArray || type.IsInterface || type.IsPointer || type.IsByRef) return false;
        if (type == typeof(string) || type == typeof(object) || type == typeof(void) || type == typeof(decimal) || type == typeof(Guid)) return false;
        if (type.IsGenericTypeDefinition || type.ContainsGenericParameters) return false;
        if (typeof(Delegate).IsAssignableFrom(type)) return false;
        if (type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
                                         ns.StartsWith("Microsoft.", StringComparison.Ordinal)))
        {
            return false;
        }

        return true;
    }

    /// <summary>The shape of one type, on the Derived rung.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The modelled types come from handler discovery, which already roots them; describe-time diagnostics on a JIT runtime, never dispatch.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The modelled types come from handler discovery, which already roots them; describe-time diagnostics on a JIT runtime, never dispatch.")]
    public static TypeShapeDescriptor Reflect(Type type, EventModelDerivation derivation)
    {
        var fields = new List<FieldDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nullability = new NullabilityInfoContext();

        // The record's positional parameters carry the attributes written on them, so the constructor
        // parameter of a member's name is read beside the member
        var parameters = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(x => x.GetParameters())
            .ToList();

        foreach (var level in hierarchyOf(type))
        {
            foreach (var member in level.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                switch (member)
                {
                    case PropertyInfo { CanRead: true } property when property.GetIndexParameters().Length == 0 &&
                                                                     property.GetMethod?.IsStatic == false &&
                                                                     seen.Add(property.Name):
                        fields.Add(field(type, property.Name, property.PropertyType, nullability.Create(property),
                            property.GetCustomAttributes(true), parameters, derivation, () => XmlDocComments.SummaryOf(property)));
                        break;

                    case FieldInfo { IsStatic: false, IsLiteral: false } fieldInfo when seen.Add(fieldInfo.Name):
                        fields.Add(field(type, fieldInfo.Name, fieldInfo.FieldType, nullability.Create(fieldInfo),
                            fieldInfo.GetCustomAttributes(true), parameters, derivation, () => XmlDocComments.SummaryOf(fieldInfo)));
                        break;
                }
            }
        }

        return new TypeShapeDescriptor(TypeDescriptor.For(type), fields) { Provenance = EventModelProvenance.Derived };
    }

    private static IEnumerable<Type> hierarchyOf(Type type)
    {
        var chain = new List<Type>();
        for (var current = type; current is not null && current != typeof(object) && current != typeof(ValueType); current = current.BaseType)
        {
            chain.Insert(0, current);
        }

        return chain;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Member types come from handler discovery, which already roots them; describe-time diagnostics on a JIT runtime, never dispatch.")]
    private static FieldDescriptor field(Type owner, string name, Type memberType, NullabilityInfo nullability,
        object[] memberAttributes, IReadOnlyList<ParameterInfo> parameters, EventModelDerivation derivation,
        Func<string?> describe)
    {
        var attributes = memberAttributes.ToList();
        var parameter = parameters.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal));
        if (parameter is not null) attributes.AddRange(parameter.GetCustomAttributes(true));

        var optional = nullability.ReadState == NullabilityState.Nullable;
        var fieldType = memberType;

        if (Nullable.GetUnderlyingType(fieldType) is { } underlying)
        {
            optional = true;
            fieldType = underlying;
        }

        var many = false;
        if (fieldType.IsArray)
        {
            many = true;
            fieldType = fieldType.GetElementType()!;
        }
        else if (fieldType != typeof(string) && elementTypeOf(fieldType) is { } element)
        {
            many = true;
            fieldType = element;
        }

        bool has<T>() where T : Attribute => attributes.OfType<T>().Any();

        var identity = has<IdentityAttribute>() || has<KeyAttribute>()
                       || string.Equals(name, "Id", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, owner.Name + "Id", StringComparison.OrdinalIgnoreCase);
        var generated = has<GeneratedAttribute>() || has<DatabaseGeneratedAttribute>();
        var pii = has<PiiAttribute>();
        if (has<RequiredAttribute>() || has<RequiredMemberAttribute>()) optional = false;

        var tag = derivation.TagTypes.Count > 0 && derivation.IsTagType(fieldType)
            ? EventTagDescriptor.NameFor(fieldType)
            : null;

        return new FieldDescriptor(name, TypeDescriptor.For(fieldType))
        {
            Cardinality = many ? FieldCardinality.Many : FieldCardinality.One,
            Optional = optional,
            Identity = identity,
            Generated = generated,
            Pii = pii,
            Tag = tag,
            Description = derivation.ReadDocComments ? describe() : null
        };
    }

    /// <summary>The <c>T</c> of an <c>IEnumerable&lt;T&gt;</c> the type is or implements, else null -- the generator's rule.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Member types come from handler discovery, which already roots them; only the interface list is read.")]
    private static Type? elementTypeOf(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }
}
