using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Wolverine.Pulsar.Schemas;

/// <summary>
/// Generates an Avro-format JSON schema string from a CLR type. Pulsar represents both its
/// <c>SchemaType.Json</c> and <c>SchemaType.Avro</c> definitions as an Avro schema, so this is what gets
/// registered with the broker for a JSON-schema'd topic (GH-3183). The generator covers the common POCO
/// shapes — primitives, strings, enums, <c>Guid</c>/<c>DateTime</c>(-ish) as strings, nullable value
/// types as <c>["null", T]</c> unions, arrays, and nested records — and falls back to <c>string</c> for
/// anything it can't map so registration never fails on an exotic property.
/// </summary>
internal static class AvroSchemaGenerator
{
    public static string Generate(Type type)
    {
        var node = BuildRecord(type, new HashSet<Type>());
        return JsonSerializer.Serialize(node);
    }

    private static object BuildRecord(Type type, HashSet<Type> seen)
    {
        // Guard against recursive types: a record that (transitively) contains itself degrades to string.
        if (!seen.Add(type))
        {
            return "string";
        }

        var fields = new List<Dictionary<string, object>>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            fields.Add(new Dictionary<string, object>
            {
                ["name"] = property.Name,
                ["type"] = MapType(property.PropertyType, seen)
            });
        }

        seen.Remove(type);

        return new Dictionary<string, object>
        {
            ["type"] = "record",
            ["name"] = SanitizeName(type.Name),
            ["namespace"] = type.Namespace ?? "wolverine",
            ["fields"] = fields
        };
    }

    private static object MapType(Type type, HashSet<Type> seen)
    {
        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            // Nullable value type -> ["null", T]
            return new object[] { "null", MapType(underlying, seen) };
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) || type == typeof(char))
        {
            return "int";
        }

        if (type == typeof(long) || type == typeof(ulong))
        {
            return "long";
        }

        if (type == typeof(float))
        {
            return "float";
        }

        if (type == typeof(double) || type == typeof(decimal))
        {
            return "double";
        }

        if (type == typeof(byte[]))
        {
            return "bytes";
        }

        // Guid / DateTime / DateTimeOffset / TimeSpan / enums and anything else scalar -> string.
        if (type.IsEnum || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(TimeSpan) || type == typeof(Uri))
        {
            return "string";
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = ElementType(type);
            return new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = element == null ? "string" : MapType(element, seen)
            };
        }

        if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
        {
            return BuildRecord(type, seen);
        }

        return "string";
    }

    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static string SanitizeName(string name)
    {
        // Avro names must be alphanumeric/underscore; strip generic-arity backticks etc.
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        return builder.Length == 0 ? "Record" : builder.ToString();
    }
}
