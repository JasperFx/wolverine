using System.Collections;
using System.Reflection;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;

namespace Wolverine.Http.Diagnostics;

/// <summary>
/// Reflects a CLR type into an <see cref="OpenApiSchemaDescriptor"/>
/// suitable for transmission to CritterWatch. Honours the depth-3
/// inline cap (Q12) — beyond that, the recursive walk emits a
/// <c>$ref</c> placeholder pointing at the type's full name. The
/// frontend renders truncated subtrees as a clickable chip.
/// </summary>
/// <remarks>
/// Best-effort. We are not trying to reproduce ASP.NET Core's full
/// schema-generation pipeline; only enough that operators can see the
/// shape. Hosts that opt into <c>Microsoft.AspNetCore.OpenApi</c> get
/// a richer flattener at the consumer side
/// (<c>Wolverine.CritterWatch.Http</c>).
/// </remarks>
internal static class SchemaFlattener
{
    public static OpenApiSchemaDescriptor For(Type? type)
    {
        if (type is null) return new OpenApiSchemaDescriptor { Type = "object" };
        return walk(type, depth: 0, seen: new HashSet<Type>());
    }

    private static OpenApiSchemaDescriptor walk(Type type, int depth, HashSet<Type> seen)
    {
        // Unwrap nullable
        var underlying = Nullable.GetUnderlyingType(type);
        var nullable = underlying is not null;
        var t = underlying ?? type;

        // Primitive / well-known
        if (TryPrimitive(t, out var primitive))
        {
            primitive.Nullable = nullable;
            return primitive;
        }

        if (t.IsEnum)
        {
            return new OpenApiSchemaDescriptor
            {
                Type = "string",
                Enum = Enum.GetNames(t).Cast<object>().ToList(),
                Nullable = nullable
            };
        }

        // Arrays / collections
        if (t.IsArray)
        {
            return new OpenApiSchemaDescriptor
            {
                Type = "array",
                Items = walk(t.GetElementType()!, depth + 1, seen),
                Nullable = nullable
            };
        }

        if (typeof(IEnumerable).IsAssignableFrom(t) && t != typeof(string))
        {
            var elem = t.IsGenericType
                ? t.GetGenericArguments().FirstOrDefault() ?? typeof(object)
                : typeof(object);

            return new OpenApiSchemaDescriptor
            {
                Type = "array",
                Items = walk(elem, depth + 1, seen),
                Nullable = nullable
            };
        }

        // Beyond the inline depth cap, emit a $ref chip — frontend
        // expands lazily.
        if (depth >= OpenApiDescriptorBuilder.MaxInlineSchemaDepth)
        {
            return new OpenApiSchemaDescriptor { Ref = t.FullNameInCode(), Nullable = nullable };
        }

        // Cycle guard — emit $ref when the same type recurs.
        if (!seen.Add(t))
        {
            return new OpenApiSchemaDescriptor { Ref = t.FullNameInCode(), Nullable = nullable };
        }

        try
        {
            var schema = new OpenApiSchemaDescriptor
            {
                Type = "object",
                Title = t.Name,
                Nullable = nullable
            };

            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                schema.Properties[prop.Name] = walk(prop.PropertyType, depth + 1, seen);
            }

            return schema;
        }
        finally
        {
            seen.Remove(t);
        }
    }

    private static bool TryPrimitive(Type t, out OpenApiSchemaDescriptor schema)
    {
        schema = null!;

        if (t == typeof(string))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string" };
            return true;
        }
        if (t == typeof(bool))
        {
            schema = new OpenApiSchemaDescriptor { Type = "boolean" };
            return true;
        }
        if (t == typeof(int) || t == typeof(short) || t == typeof(byte))
        {
            schema = new OpenApiSchemaDescriptor { Type = "integer", Format = "int32" };
            return true;
        }
        if (t == typeof(long))
        {
            schema = new OpenApiSchemaDescriptor { Type = "integer", Format = "int64" };
            return true;
        }
        if (t == typeof(float))
        {
            schema = new OpenApiSchemaDescriptor { Type = "number", Format = "float" };
            return true;
        }
        if (t == typeof(double) || t == typeof(decimal))
        {
            schema = new OpenApiSchemaDescriptor { Type = "number", Format = "double" };
            return true;
        }
        if (t == typeof(Guid))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string", Format = "uuid" };
            return true;
        }
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string", Format = "date-time" };
            return true;
        }
        if (t == typeof(DateOnly))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string", Format = "date" };
            return true;
        }
        if (t == typeof(TimeOnly) || t == typeof(TimeSpan))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string", Format = "time" };
            return true;
        }
        if (t == typeof(Uri))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string", Format = "uri" };
            return true;
        }
        if (t == typeof(byte[]))
        {
            schema = new OpenApiSchemaDescriptor { Type = "string", Format = "byte" };
            return true;
        }

        return false;
    }
}
