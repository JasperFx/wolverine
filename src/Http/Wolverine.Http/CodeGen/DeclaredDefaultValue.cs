using System.Globalization;
using System.Reflection;
using System.Text;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.CodeGen;

/// <summary>
/// GH-5099: renders a parameter's declared C# default value (<c>int limit = 100</c>,
/// <c>string name = "all"</c>) as the source literal that initializes the bound variable, so a value
/// missing from the request keeps the declared default the way ASP.NET Core Minimal APIs do.
/// </summary>
internal static class DeclaredDefaultValue
{
    /// <summary>
    /// The C# expression for the parameter's declared default, or null when the parameter declares none
    /// or the default is the type's own default (nothing to emit beyond <c>default</c>).
    /// </summary>
    public static string? CodeFor(ParameterInfo parameter)
    {
        if (!parameter.HasDefaultValue) return null;

        var value = parameter.DefaultValue;
        if (value == null || value is DBNull || value == Missing.Value) return null;

        return TryWriteLiteral(parameter.ParameterType, value, out var code) ? code : null;
    }

    public static bool TryWriteLiteral(Type parameterType, object value, out string code)
    {
        code = string.Empty;
        var type = parameterType.IsNullable() ? parameterType.GetInnerTypeFromNullable() : parameterType;

        if (type.IsEnum)
        {
            // The declared default of an enum parameter is reflected as the enum value (or its underlying
            // number), so cast the underlying number back to the enum type
            var underlying = Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
            code = $"({type.FullNameInCode()})({Convert.ToString(underlying, CultureInfo.InvariantCulture)})";
            return true;
        }

        switch (value)
        {
            case string s:
                code = "\"" + escape(s, '"') + "\"";
                return true;
            case char c:
                code = "'" + escape(c.ToString(), '\'') + "'";
                return true;
            case bool b:
                code = b ? "true" : "false";
                return true;
            case int i:
                code = i.ToString(CultureInfo.InvariantCulture);
                return true;
            case long l:
                code = l.ToString(CultureInfo.InvariantCulture) + "L";
                return true;
            case uint ui:
                code = ui.ToString(CultureInfo.InvariantCulture) + "U";
                return true;
            case ulong ul:
                code = ul.ToString(CultureInfo.InvariantCulture) + "UL";
                return true;
            case short or ushort or byte or sbyte:
                code = $"({value.GetType().FullNameInCode()})({Convert.ToString(value, CultureInfo.InvariantCulture)})";
                return true;
            case double d when double.IsFinite(d):
                code = d.ToString("R", CultureInfo.InvariantCulture) + "D";
                return true;
            case float f when float.IsFinite(f):
                code = f.ToString("R", CultureInfo.InvariantCulture) + "F";
                return true;
            case decimal m:
                code = m.ToString(CultureInfo.InvariantCulture) + "M";
                return true;
        }

        return false;
    }

    private static string escape(string value, char quote)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\0': builder.Append("\\0"); break;
                default:
                    if (c == quote)
                    {
                        builder.Append('\\').Append(c);
                    }
                    else if (char.IsControl(c) || char.IsSurrogate(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }
}
